package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/signal"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	lark "github.com/larksuite/oapi-sdk-go/v3"
	"github.com/larksuite/oapi-sdk-go/v3/channel/normalize"
	channeltypes "github.com/larksuite/oapi-sdk-go/v3/channel/types"
	larkcore "github.com/larksuite/oapi-sdk-go/v3/core"
	"github.com/larksuite/oapi-sdk-go/v3/event/dispatcher"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
	larkws "github.com/larksuite/oapi-sdk-go/v3/ws"
)

type startupConfig struct {
	AppID     string `json:"app_id"`
	AppSecret string `json:"app_secret"`
}

type outputEvent struct {
	Type        string `json:"type"`
	RequestID   string `json:"request_id,omitempty"`
	ChatType    string `json:"chat_type,omitempty"`
	SenderType  string `json:"sender_type,omitempty"`
	MessageType string `json:"message_type,omitempty"`
	MessageID   string `json:"message_id,omitempty"`
	SenderID    string `json:"sender_id,omitempty"`
	Content     string `json:"content,omitempty"`
	State       string `json:"state,omitempty"`
}

type inputCommand struct {
	Type           string `json:"type"`
	RequestID      string `json:"request_id"`
	MessageID      string `json:"message_id"`
	Text           string `json:"text"`
	IdempotencyKey string `json:"idempotency_key"`
}

type replySender func(context.Context, string, string, string) error

type discardLogger struct{}

func (discardLogger) Debug(context.Context, ...interface{}) {}
func (discardLogger) Info(context.Context, ...interface{})  {}
func (discardLogger) Warn(context.Context, ...interface{})  {}
func (discardLogger) Error(context.Context, ...interface{}) {}

var outputMu sync.Mutex

func emit(event outputEvent) error {
	outputMu.Lock()
	defer outputMu.Unlock()
	return json.NewEncoder(os.Stdout).Encode(event)
}

func normalizeEvent(event *larkim.P2MessageReceiveV1) (outputEvent, bool) {
	msg := normalize.ParseMessage(event)
	if msg == nil {
		return outputEvent{}, false
	}
	senderType := ""
	if event.Event != nil && event.Event.Sender != nil && event.Event.Sender.SenderType != nil {
		senderType = *event.Event.Sender.SenderType
	}
	return outputEvent{
		Type:        "im.message.receive_v1",
		ChatType:    msg.ChatType,
		SenderType:  senderType,
		MessageType: msg.RawContentType,
		MessageID:   msg.MessageID,
		SenderID:    msg.UserID,
		Content:     stripOfficialMentionKeys(msg.Content, msg.Mentions),
	}, true
}

func stripOfficialMentionKeys(content string, mentions []channeltypes.Mention) string {
	cleaned := content
	removed := false
	for _, mention := range mentions {
		if mention.Key == "" {
			continue
		}
		var keyRemoved bool
		cleaned, keyRemoved = removeStandaloneMentionKey(cleaned, mention.Key)
		removed = removed || keyRemoved
	}
	if !removed {
		return content
	}
	return strings.TrimSpace(cleaned)
}

func removeStandaloneMentionKey(content, key string) (string, bool) {
	searchFrom := 0
	for searchFrom < len(content) {
		relativeIndex := strings.Index(content[searchFrom:], key)
		if relativeIndex < 0 {
			break
		}
		index := searchFrom + relativeIndex
		end := index + len(key)
		if isMentionBoundaryBefore(content, index) && isMentionBoundaryAfter(content, end) {
			return content[:index] + content[end:], true
		}
		searchFrom = end
	}
	return content, false
}

func isMentionBoundaryBefore(content string, index int) bool {
	if index == 0 {
		return true
	}
	r, _ := utf8.DecodeLastRuneInString(content[:index])
	return !isMentionKeyRune(r)
}

func isMentionBoundaryAfter(content string, index int) bool {
	if index == len(content) {
		return true
	}
	r, _ := utf8.DecodeRuneInString(content[index:])
	return !isMentionKeyRune(r)
}

func isMentionKeyRune(r rune) bool {
	return r == '@' || r == '_' || r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9'
}

func validOpaqueRequestID(value string) bool {
	if len(value) != 32 {
		return false
	}
	for _, r := range value {
		if !(r >= '0' && r <= '9') && !(r >= 'a' && r <= 'f') {
			return false
		}
	}
	return true
}

func processReplyCommand(ctx context.Context, command inputCommand, send replySender) (outputEvent, bool) {
	if command.Type != "reply" || !validOpaqueRequestID(command.RequestID) {
		return outputEvent{}, false
	}

	result := outputEvent{
		Type:      "command_result",
		RequestID: command.RequestID,
		State:     "failed",
	}
	if command.MessageID == "" || command.Text == "" || command.IdempotencyKey == "" || len(command.Text) > 4000 || len(command.IdempotencyKey) > 64 {
		return result, true
	}
	if err := send(ctx, command.MessageID, command.Text, command.IdempotencyKey); err != nil {
		return result, true
	}

	result.State = "succeeded"
	return result, true
}

func consumeCommands(ctx context.Context, decoder *json.Decoder, send replySender, stop context.CancelFunc) {
	defer stop()
	for {
		var command inputCommand
		if err := decoder.Decode(&command); err != nil {
			return
		}

		commandCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
		result, ok := processReplyCommand(commandCtx, command, send)
		cancel()
		if ok {
			_ = emit(result)
		}
	}
}

func main() {
	var cfg startupConfig
	decoder := json.NewDecoder(os.Stdin)
	if err := decoder.Decode(&cfg); err != nil {
		fmt.Fprintln(os.Stderr, "startup config could not be decoded")
		os.Exit(2)
	}
	if cfg.AppID == "" || cfg.AppSecret == "" {
		fmt.Fprintln(os.Stderr, "startup config is incomplete")
		os.Exit(2)
	}

	apiClient := lark.NewClient(
		cfg.AppID,
		cfg.AppSecret,
		lark.WithOpenBaseUrl("https://open.feishu.cn"),
		lark.WithLogger(discardLogger{}),
		lark.WithLogLevel(larkcore.LogLevelError),
	)
	sendReply := func(ctx context.Context, messageID, text, idempotencyKey string) error {
		content, err := json.Marshal(struct {
			Text string `json:"text"`
		}{Text: text})
		if err != nil {
			return errors.New("reply content encoding failed")
		}
		req := larkim.NewReplyMessageReqBuilder().
			MessageId(messageID).
			Body(larkim.NewReplyMessageReqBodyBuilder().
				Content(string(content)).
				MsgType("text").
				Uuid(idempotencyKey).
				Build()).
			Build()
		resp, err := apiClient.Im.Message.Reply(ctx, req)
		if err != nil || !resp.Success() {
			return errors.New("reply request failed")
		}
		return nil
	}

	handler := dispatcher.NewEventDispatcher("", "").OnP2MessageReceiveV1(
		func(ctx context.Context, event *larkim.P2MessageReceiveV1) error {
			output, ok := normalizeEvent(event)
			if !ok {
				return nil
			}
			return emit(output)
		},
	)
	wsClient := larkws.NewClient(
		cfg.AppID,
		cfg.AppSecret,
		larkws.WithDomain("https://open.feishu.cn"),
		larkws.WithEventHandler(handler),
		larkws.WithLogger(discardLogger{}),
		larkws.WithLogLevel(larkcore.LogLevelError),
		larkws.WithOnReady(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "ready"})
		}),
		larkws.WithOnReconnecting(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "reconnecting"})
		}),
		larkws.WithOnReconnected(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "ready"})
		}),
		larkws.WithOnDisconnected(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "disconnected"})
		}),
	)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	go consumeCommands(ctx, decoder, sendReply, stop)
	if err := wsClient.Start(ctx); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, "websocket lifecycle stopped")
		os.Exit(1)
	}
}
