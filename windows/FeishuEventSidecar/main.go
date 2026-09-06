package main

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	lark "github.com/larksuite/oapi-sdk-go/v3"
	"github.com/larksuite/oapi-sdk-go/v3/channel/normalize"
	channeltypes "github.com/larksuite/oapi-sdk-go/v3/channel/types"
	larkcore "github.com/larksuite/oapi-sdk-go/v3/core"
	"github.com/larksuite/oapi-sdk-go/v3/event/dispatcher"
	"github.com/larksuite/oapi-sdk-go/v3/event/dispatcher/callback"
	larkapplication "github.com/larksuite/oapi-sdk-go/v3/service/application/v6"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
	larkws "github.com/larksuite/oapi-sdk-go/v3/ws"
)

type startupConfig struct {
	AppID                        string `json:"app_id"`
	AppSecret                    string `json:"app_secret"`
	RuntimeNonce                 string `json:"runtime_nonce"`
	IdentityFingerprint          string `json:"identity_fingerprint"`
	IdentityGeneration           string `json:"identity_generation"`
	ParentPID                    int    `json:"parent_pid"`
	ParentStartTimeUTCTicks      int64  `json:"parent_start_time_utc_ticks"`
	ParentExecutableFingerprint  string `json:"parent_executable_sha256"`
	ParentPayloadManifest        string `json:"parent_payload_manifest_sha256"`
	SidecarExecutableFingerprint string `json:"sidecar_executable_sha256"`
}

type outputEvent struct {
	Resources                    []resourceReference `json:"resources,omitempty"`
	ResourceError                string              `json:"resource_error,omitempty"`
	ResourceErrorCode            string              `json:"resource_error_code,omitempty"`
	ResourceErrorPath            string              `json:"resource_error_path,omitempty"`
	ChatID                       string              `json:"chat_id,omitempty"`
	Type                         string              `json:"type"`
	RequestID                    string              `json:"request_id,omitempty"`
	ChatType                     string              `json:"chat_type,omitempty"`
	SenderType                   string              `json:"sender_type,omitempty"`
	MessageType                  string              `json:"message_type,omitempty"`
	MessageID                    string              `json:"message_id,omitempty"`
	SenderID                     string              `json:"sender_id,omitempty"`
	Content                      string              `json:"content,omitempty"`
	CardToken                    string              `json:"card_token,omitempty"`
	ContentState                 string              `json:"content_state,omitempty"`
	State                        string              `json:"state,omitempty"`
	ErrorStage                   string              `json:"error_stage,omitempty"`
	ErrorCode                    int                 `json:"error_code,omitempty"`
	ErrorDiagnostic              string              `json:"error_diagnostic,omitempty"`
	Operation                    string              `json:"operation,omitempty"`
	MenuStage                    string              `json:"menu_stage,omitempty"`
	MenuDiff                     string              `json:"menu_diff,omitempty"`
	VersionState                 string              `json:"version_state,omitempty"`
	Version                      string              `json:"version,omitempty"`
	RuntimeNonce                 string              `json:"runtime_nonce,omitempty"`
	IdentityFingerprint          string              `json:"identity_fingerprint,omitempty"`
	IdentityGeneration           string              `json:"identity_generation,omitempty"`
	ParentPID                    int                 `json:"parent_pid,omitempty"`
	ParentStartTimeUTCTicks      int64               `json:"parent_start_time_utc_ticks,omitempty"`
	ParentExecutableFingerprint  string              `json:"parent_executable_sha256,omitempty"`
	ParentPayloadManifest        string              `json:"parent_payload_manifest_sha256,omitempty"`
	RegisteredConsumers          []string            `json:"registered_consumers,omitempty"`
	SidecarExecutableFingerprint string              `json:"sidecar_executable_sha256,omitempty"`
}

var registeredConsumers = []string{
	"im.message.receive_v1",
	"application.bot.menu_v6",
	"card.action.trigger",
}

type botInfoEnvelope struct {
	Code int `json:"code"`
	Bot  struct {
		OpenID  string `json:"open_id"`
		AppName string `json:"app_name"`
	} `json:"bot"`
}

type inputCommand struct {
	TargetRequestID            string `json:"target_request_id,omitempty"`
	FileKey                    string `json:"file_key"`
	ResourceKind               string `json:"resource_kind"`
	OutputPath                 string `json:"output_path"`
	Path                       string `json:"path"`
	FileName                   string `json:"file_name"`
	Type                       string `json:"type"`
	RequestID                  string `json:"request_id"`
	MessageID                  string `json:"message_id"`
	RecipientID                string `json:"recipient_id"`
	Text                       string `json:"text"`
	Card                       string `json:"card"`
	IdempotencyKey             string `json:"idempotency_key"`
	ExpectedIdentityGeneration string `json:"expected_identity_generation"`
}

type replySender func(context.Context, string, string, string) error
type cardSender func(context.Context, string, string, string) error

type replyFailure struct {
	stage      string
	code       int
	diagnostic string
}

func (failure *replyFailure) Error() string {
	return "reply failed"
}

func safeFieldValidationDiagnostic(code int, message string, violations []*larkcore.CodeErrorFieldViolation) string {
	if code != 99992402 {
		return ""
	}
	parts := []string{"field_validation_failed"}
	seen := make(map[string]bool)
	for _, violation := range violations {
		if violation == nil {
			continue
		}
		field := strings.TrimSpace(violation.Field)
		if len(field) == 0 || len(field) > 128 || seen[field] {
			continue
		}
		safe := true
		for _, r := range field {
			if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') ||
				(r >= '0' && r <= '9') || r == '.' || r == '[' || r == ']' || r == '_' || r == '-') {
				safe = false
				break
			}
		}
		if !safe {
			continue
		}
		seen[field] = true
		rule := "invalid"
		description := strings.ToLower(violation.Description)
		switch {
		case strings.Contains(description, "display") || strings.Contains(violation.Description, "展示"):
			rule = "display_strategy"
		case strings.Contains(description, "parent_menu_id") || strings.Contains(description, "parent menu id"):
			rule = "parent_menu_id"
		case strings.Contains(description, "menu_id") || strings.Contains(description, "menu id"):
			rule = "menu_id"
		case strings.Contains(description, "first-level") || strings.Contains(description, "first level") || strings.Contains(violation.Description, "一级"):
			rule = "top_level_count"
		case strings.Contains(description, "second-level") || strings.Contains(description, "second level") || strings.Contains(description, "child") || strings.Contains(violation.Description, "二级"):
			rule = "child_count"
		case strings.Contains(description, "count") || strings.Contains(description, "number") || strings.Contains(description, " num ") || strings.Contains(violation.Description, "数量") || strings.Contains(violation.Description, "最多"):
			rule = "count_limit"
		case strings.Contains(description, "length") || strings.Contains(violation.Description, "长度"):
			rule = "length"
		case strings.Contains(description, "format") || strings.Contains(description, "pattern") || strings.Contains(violation.Description, "格式"):
			rule = "format"
		case strings.Contains(description, "required") || strings.Contains(violation.Description, "必填"):
			rule = "required"
		case strings.Contains(description, "range") || strings.Contains(violation.Description, "范围"):
			rule = "range"
		case strings.Contains(description, "duplicate") || strings.Contains(violation.Description, "重复"):
			rule = "duplicate"
		}
		if rule == "invalid" && strings.HasPrefix(field, "bot.bot_menus") {
			if detail := safeMenuValidationDescription(violation.Description); detail != "" {
				rule = "invalid_" + detail
			}
		}
		parts = append(parts, field+":"+rule)
		if len(parts) == 9 {
			break
		}
	}
	if len(parts) == 1 {
		if detail := safeMenuValidationDescription(message); detail != "" {
			parts = append(parts, "message:"+detail)
		}
	}
	return strings.Join(parts, ";")
}

func safeRawFieldValidationDiagnostic(rawBody []byte, fallbackCode int, fallbackMessage string) string {
	var envelope struct {
		Code  int    `json:"code"`
		Msg   string `json:"msg"`
		Error *struct {
			FieldViolations []*larkcore.CodeErrorFieldViolation `json:"field_violations"`
		} `json:"error"`
	}
	if len(rawBody) == 0 || json.Unmarshal(rawBody, &envelope) != nil {
		return safeFieldValidationDiagnostic(fallbackCode, fallbackMessage, nil)
	}
	code := envelope.Code
	if code == 0 {
		code = fallbackCode
	}
	message := envelope.Msg
	if message == "" {
		message = fallbackMessage
	}
	var violations []*larkcore.CodeErrorFieldViolation
	if envelope.Error != nil {
		violations = envelope.Error.FieldViolations
	}
	return safeFieldValidationDiagnostic(code, message, violations)
}

func safeMenuValidationDescription(value string) string {
	normalized := strings.Join(strings.Fields(value), " ")
	lower := strings.ToLower(normalized)
	if normalized == "" || strings.Contains(lower, "secret") || strings.Contains(lower, "token") || strings.Contains(lower, "cli_") {
		return ""
	}
	runes := make([]rune, 0, len(normalized))
	for _, r := range normalized {
		if unicode.IsLetter(r) || unicode.IsNumber(r) || strings.ContainsRune(" _-.:,()[]", r) {
			runes = append(runes, r)
		}
		if len(runes) == 160 {
			break
		}
	}
	return strings.ReplaceAll(strings.TrimSpace(string(runes)), " ", "_")
}

func safePermissionDiagnostic(
	code int,
	message string,
	details []*larkcore.CodeErrorDetail,
	violations []*larkcore.CodeErrorPermissionViolation,
) string {
	if code != 99991672 {
		return ""
	}
	text := message
	for _, detail := range details {
		if detail != nil {
			text += " " + detail.Key + " " + detail.Value
		}
	}
	for _, violation := range violations {
		if violation != nil {
			text += " " + violation.Type + " " + violation.Subject + " " + violation.Description
		}
	}
	allowed := []string{
		"application:application:self_manage",
		"application:bot.menu:write",
		"application:bot.menu:readonly",
		"admin:app.info:readonly",
	}
	found := make([]string, 0, len(allowed))
	for _, scope := range allowed {
		if strings.Contains(text, scope) {
			found = append(found, scope)
		}
	}
	if len(found) == 0 {
		return "required_scope:unknown"
	}
	return "required_scope:" + strings.Join(found, ",")
}

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
	if event != nil && event.Event != nil && event.Event.Message != nil &&
		event.Event.Message.MessageType != nil && *event.Event.Message.MessageType != "text" {
		return normalizeResourceEvent(event)
	}
	msg := normalize.ParseMessage(event)
	if msg == nil {
		return outputEvent{}, false
	}
	content := msg.Content
	contentState := "sdk-normalized"
	messageType := msg.RawContentType
	if msg.RawContentType == "text" {
		if event.Event == nil || event.Event.Message == nil || event.Event.Message.Content == nil ||
			len(*event.Event.Message.Content) > 16*1024 {
			return outputEvent{}, false
		}
		decoded, ok := decodeTextEnvelope([]byte(*event.Event.Message.Content))
		if !ok {
			return outputEvent{}, false
		}
		content = decoded
		contentState = "text-json-decoded"
	} else if msg.RawContentType == "post" {
		if event.Event == nil || event.Event.Message == nil || event.Event.Message.Content == nil ||
			len(*event.Event.Message.Content) > 16*1024 {
			return outputEvent{}, false
		}
		decoded, ok := decodePlainTextPostEnvelope([]byte(*event.Event.Message.Content))
		if !ok {
			return outputEvent{}, false
		}
		content = decoded
		contentState = "post-text-decoded"
		messageType = "text"
	}
	senderType := ""
	if event.Event != nil && event.Event.Sender != nil && event.Event.Sender.SenderType != nil {
		senderType = *event.Event.Sender.SenderType
	}
	return outputEvent{
		Type:         "im.message.receive_v1",
		ChatType:     msg.ChatType,
		SenderType:   senderType,
		MessageType:  messageType,
		MessageID:    msg.MessageID,
		SenderID:     msg.UserID,
		Content:      stripOfficialMentionKeys(content, msg.Mentions),
		ContentState: contentState,
	}, true
}

func decodeTextEnvelope(raw []byte) (string, bool) {
	var envelope map[string]json.RawMessage
	if json.Unmarshal(raw, &envelope) != nil || len(envelope) != 1 {
		return "", false
	}
	rawText, ok := envelope["text"]
	if !ok {
		return "", false
	}
	var text string
	if json.Unmarshal(rawText, &text) != nil || strings.TrimSpace(text) == "" {
		return "", false
	}
	return text, true
}

// decodePlainTextPostEnvelope accepts only the unambiguous subset of Feishu's
// rich-text message format: an optional title and paragraphs made exclusively
// from text nodes. Interactive or identity-bearing nodes (links, images, @,
// media, code blocks, and unknown fields) remain fail-closed.
func decodePlainTextPostEnvelope(raw []byte) (string, bool) {
	var envelope map[string]json.RawMessage
	if json.Unmarshal(raw, &envelope) != nil || len(envelope) == 0 {
		return "", false
	}
	if len(envelope) == 1 {
		for key, value := range envelope {
			if isFeishuLocaleKey(key) {
				return decodePlainTextPostBody(value)
			}
		}
	}
	return decodePlainTextPostBody(raw)
}

func decodePlainTextPostBody(raw []byte) (string, bool) {
	var envelope map[string]json.RawMessage
	if json.Unmarshal(raw, &envelope) != nil || len(envelope) == 0 || len(envelope) > 2 {
		return "", false
	}
	for key := range envelope {
		if key != "title" && key != "content" {
			return "", false
		}
	}
	if rawTitle, ok := envelope["title"]; ok {
		var title string
		if json.Unmarshal(rawTitle, &title) != nil || strings.TrimSpace(title) != "" {
			return "", false
		}
	}
	rawContent, ok := envelope["content"]
	if !ok {
		return "", false
	}
	var paragraphs [][]map[string]json.RawMessage
	if json.Unmarshal(rawContent, &paragraphs) != nil || len(paragraphs) == 0 {
		return "", false
	}
	var result strings.Builder
	for paragraphIndex, paragraph := range paragraphs {
		if len(paragraph) == 0 {
			return "", false
		}
		if paragraphIndex > 0 {
			result.WriteByte('\n')
		}
		for _, node := range paragraph {
			if len(node) < 2 || len(node) > 3 {
				return "", false
			}
			for key := range node {
				if key != "tag" && key != "text" && key != "style" {
					return "", false
				}
			}
			var tag string
			var text string
			if json.Unmarshal(node["tag"], &tag) != nil || tag != "text" ||
				json.Unmarshal(node["text"], &text) != nil {
				return "", false
			}
			if rawStyle, ok := node["style"]; ok {
				var styles []string
				if json.Unmarshal(rawStyle, &styles) != nil {
					return "", false
				}
				for _, style := range styles {
					if style != "bold" && style != "italic" && style != "underline" && style != "lineThrough" {
						return "", false
					}
				}
			}
			result.WriteString(text)
		}
	}
	decoded := strings.TrimSpace(result.String())
	if decoded == "" || len(decoded) > 8*1024 {
		return "", false
	}
	return decoded, true
}

func isFeishuLocaleKey(value string) bool {
	if len(value) != 5 || value[2] != '_' {
		return false
	}
	for index, character := range value {
		if index == 2 {
			continue
		}
		if character < 'a' || character > 'z' {
			return false
		}
	}
	return true
}

func normalizeMenuEvent(event *larkapplication.P2BotMenuV6) (outputEvent, bool) {
	if event == nil || event.EventV2Base == nil || event.EventV2Base.Header == nil || event.EventV2Base.Header.EventID == "" || event.Event == nil || event.Event.EventKey == nil || event.Event.Operator == nil || event.Event.Operator.OperatorId == nil || event.Event.Operator.OperatorId.OpenId == nil {
		return outputEvent{}, false
	}
	return outputEvent{Type: "application.bot.menu_v6", SenderType: "user", MessageID: event.EventV2Base.Header.EventID, SenderID: *event.Event.Operator.OperatorId.OpenId, Content: *event.Event.EventKey}, true
}

func normalizeCardEvent(event *callback.CardActionTriggerEvent) (outputEvent, bool) {
	if event == nil || event.EventV2Base == nil || event.EventV2Base.Header == nil ||
		event.EventV2Base.Header.EventID == "" || event.Event == nil || event.Event.Operator == nil ||
		event.Event.Operator.OpenID == "" || event.Event.Action == nil {
		return outputEvent{}, false
	}
	// The token is an in-memory, one-use Bridge card capability.  It is not a
	// Feishu credential and must be exact rather than inferred from a card name.
	token, ok := event.Event.Action.Value["deepori_token"].(string)
	if !ok && event.Event.Action.Tag == "select_static" {
		token = event.Event.Action.Option
		ok = token != ""
	}
	if !ok || len(token) != 32 || !isLowerHex(token) || len(event.Event.Action.Name) > 128 {
		return outputEvent{}, false
	}
	actionName := event.Event.Action.Name
	if event.Event.Action.Tag == "select_static" {
		// Selector option values are already opaque, one-use capabilities.  The
		// visible selector name is presentation metadata and must not override
		// the action bound to that capability in the WinUI process.
		actionName = ""
	}
	return outputEvent{
		Type: "card.action.trigger", SenderType: "user", MessageID: event.EventV2Base.Header.EventID,
		SenderID: event.Event.Operator.OpenID, Content: actionName, CardToken: token,
	}, true
}

func isLowerHex(value string) bool {
	for _, character := range value {
		if !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') {
			return false
		}
	}
	return true
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

func identityFingerprint(appID, botOpenID, botDisplayName string) string {
	payload := strings.Join([]string{
		"DeepOriBridge.FeishuIdentity.v1",
		strings.TrimSpace(appID),
		strings.TrimSpace(botOpenID),
		strings.TrimSpace(botDisplayName),
	}, "\x00")
	return strings.ToUpper(fmt.Sprintf("%x", sha256.Sum256([]byte(payload))))
}

func fixedTimeEqual(left, right string) bool {
	if len(left) != len(right) {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(left), []byte(right)) == 1
}

func currentExecutableFingerprint() (string, error) {
	path, err := os.Executable()
	if err != nil {
		return "", err
	}
	absolute, err := filepath.Abs(path)
	if err != nil {
		return "", err
	}
	resolved, err := filepath.EvalSymlinks(absolute)
	if err != nil || !strings.EqualFold(absolute, resolved) {
		return "", errors.New("sidecar executable path is not canonical")
	}
	content, err := os.ReadFile(resolved)
	if err != nil {
		return "", err
	}
	return strings.ToUpper(fmt.Sprintf("%x", sha256.Sum256(content))), nil
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
		var failure *replyFailure
		if errors.As(err, &failure) {
			result.ErrorStage = failure.stage
			result.ErrorCode = failure.code
		}
		return result, true
	}

	result.State = "succeeded"
	return result, true
}

func processP2PCommand(ctx context.Context, command inputCommand, send replySender) (outputEvent, bool) {
	if command.Type != "send_p2p" || !validOpaqueRequestID(command.RequestID) {
		return outputEvent{}, false
	}
	result := outputEvent{Type: "command_result", Operation: "send_p2p", RequestID: command.RequestID, State: "failed"}
	if command.RecipientID == "" || command.Text == "" || command.IdempotencyKey == "" || len(command.RecipientID) > 256 || len(command.Text) > 4000 || len(command.IdempotencyKey) > 64 {
		return result, true
	}
	if err := send(ctx, command.RecipientID, command.Text, command.IdempotencyKey); err != nil {
		var failure *replyFailure
		if errors.As(err, &failure) {
			result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		}
		return result, true
	}
	result.State = "succeeded"
	return result, true
}

func processCardCommand(ctx context.Context, command inputCommand, send cardSender) (outputEvent, bool) {
	if command.Type != "send_card" || !validOpaqueRequestID(command.RequestID) {
		return outputEvent{}, false
	}
	result := outputEvent{Type: "command_result", Operation: "send_card", RequestID: command.RequestID, State: "failed"}
	if command.RecipientID == "" || command.Card == "" || command.IdempotencyKey == "" || len(command.RecipientID) > 256 || len(command.Card) > 12000 || len(command.IdempotencyKey) > 64 || !json.Valid([]byte(command.Card)) {
		return result, true
	}
	if err := send(ctx, command.RecipientID, command.Card, command.IdempotencyKey); err != nil {
		var failure *replyFailure
		if errors.As(err, &failure) {
			result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		}
		return result, true
	}
	result.State = "succeeded"
	return result, true
}

func processCommand(ctx context.Context, command inputCommand, sendReply, sendP2P replySender, sendCard cardSender, menus *menuManager, taskCards ...*taskCardTransport) (outputEvent, bool) {
	if len(taskCards) == 1 && taskCards[0] != nil {
		if result, ok := taskCards[0].process(ctx, command); ok {
			return result, true
		}
	}
	if command.Type == "resource_download" || command.Type == "resource_send" {
		return (*resourceTransport)(nil).process(ctx, command)
	}
	if result, ok := processReplyCommand(ctx, command, sendReply); ok {
		result.Operation = "reply"
		return result, true
	}
	if result, ok := processP2PCommand(ctx, command, sendP2P); ok {
		return result, true
	}
	if result, ok := processCardCommand(ctx, command, sendCard); ok {
		return result, true
	}
	if !validOpaqueRequestID(command.RequestID) || menus == nil {
		return outputEvent{}, false
	}
	var result outputEvent
	var ok bool
	switch command.Type {
	case "menu_inspect":
		result, ok = menus.inspectReadOnly(ctx, command.ExpectedIdentityGeneration)
	case "menu_inspect_apply":
		result, ok = menus.inspectAndApply(ctx, command.ExpectedIdentityGeneration)
	case "menu_publish":
		result, ok = menus.publish(ctx, command.ExpectedIdentityGeneration)
	case "menu_status":
		result, ok = menus.status(ctx, command.ExpectedIdentityGeneration)
	default:
		return outputEvent{}, false
	}
	if ok {
		result.RequestID = command.RequestID
	}
	return result, ok
}

func consumeCommands(ctx context.Context, decoder *json.Decoder, sendReply, sendP2P replySender, sendCard cardSender, menus *menuManager, stop context.CancelFunc, taskCards ...*taskCardTransport) {
	defer stop()
	menuCommands := newCancelableMenuCommands()
	defer menuCommands.close()
	resourceCtx, cancelResources := context.WithCancel(ctx)
	var resourceWorkers sync.WaitGroup
	defer func() { cancelResources(); resourceWorkers.Wait() }()
	resourceSlots := make(chan struct{}, 2)
	for {
		var command inputCommand
		if err := decoder.Decode(&command); err != nil {
			return
		}
		if command.Type == "cancel_command" {
			if validOpaqueRequestID(command.RequestID) && validOpaqueRequestID(command.TargetRequestID) && menus != nil && command.ExpectedIdentityGeneration == menus.cfg.IdentityGeneration {
				menuCommands.cancel(command.TargetRequestID)
				_ = emit(outputEvent{Type: "command_result", RequestID: command.RequestID, Operation: command.Type, State: "succeeded"})
			}
			continue
		}
		if command.Type == "menu_inspect" || command.Type == "menu_inspect_apply" || command.Type == "menu_publish" || command.Type == "menu_status" {
			if !validOpaqueRequestID(command.RequestID) {
				continue
			}
			if !menuCommands.start(ctx, command.RequestID, func(commandCtx context.Context) {
				result, ok := processCommand(commandCtx, command, sendReply, sendP2P, sendCard, menus, taskCards...)
				if ok {
					_ = emit(result)
				}
			}) {
				_ = emit(outputEvent{Type: "command_result", RequestID: command.RequestID, Operation: command.Type, State: "failed", ErrorStage: "busy"})
			}
			continue
		}
		if command.Type == "resource_download" || command.Type == "resource_send" {
			if !validOpaqueRequestID(command.RequestID) {
				continue
			}
			select {
			case resourceSlots <- struct{}{}:
				resourceWorkers.Add(1)
				go func(command inputCommand) {
					defer resourceWorkers.Done()
					defer func() { <-resourceSlots }()
					result, ok := processCommand(resourceCtx, command, sendReply, sendP2P, sendCard, menus, taskCards...)
					if ok {
						_ = emit(result)
					}
				}(command)
			default:
				_ = emit(outputEvent{Type: "command_result", RequestID: command.RequestID, Operation: command.Type, State: "failed", ErrorStage: "busy"})
			}
			continue
		}

		// Application ability inspection/PATCH can legitimately take longer than
		// an ordinary message reply. Keep one bounded command alive long enough
		// to return Feishu's actual validation result to the Windows UI.
		timeout := 60 * time.Second
		if command.Type == "resource_download" || command.Type == "resource_send" {
			timeout = 120 * time.Second
		}
		commandCtx, cancel := context.WithTimeout(ctx, timeout)
		result, ok := processCommand(commandCtx, command, sendReply, sendP2P, sendCard, menus, taskCards...)
		cancel()
		if ok {
			_ = emit(result)
		}
	}
}

func queryBotIdentity(ctx context.Context, apiClient *lark.Client) (botInfoEnvelope, *replyFailure) {
	identityResponse, err := apiClient.Get(ctx, "/open-apis/bot/v3/info", nil, larkcore.AccessTokenTypeTenant)
	if err != nil || identityResponse == nil {
		return botInfoEnvelope{}, &replyFailure{stage: "transport"}
	}
	if identityResponse.StatusCode >= 400 {
		return botInfoEnvelope{}, &replyFailure{stage: "api", code: identityResponse.StatusCode}
	}
	var botIdentity botInfoEnvelope
	if err := json.Unmarshal(identityResponse.RawBody, &botIdentity); err != nil || botIdentity.Code != 0 || botIdentity.Bot.OpenID == "" || len(botIdentity.Bot.OpenID) > 256 || botIdentity.Bot.AppName == "" || len(botIdentity.Bot.AppName) > 120 {
		return botInfoEnvelope{}, &replyFailure{stage: "identity"}
	}
	return botIdentity, nil
}

func verifyRuntimeIdentity(ctx context.Context, apiClient *lark.Client, cfg startupConfig) *replyFailure {
	botIdentity, failure := queryBotIdentity(ctx, apiClient)
	if failure != nil {
		return failure
	}
	actual := identityFingerprint(cfg.AppID, botIdentity.Bot.OpenID, botIdentity.Bot.AppName)
	if !fixedTimeEqual(actual, cfg.IdentityFingerprint) {
		return &replyFailure{stage: "identity"}
	}
	return nil
}

func main() {
	var cfg startupConfig
	decoder := json.NewDecoder(os.Stdin)
	if err := decoder.Decode(&cfg); err != nil {
		fmt.Fprintln(os.Stderr, "startup config could not be decoded")
		os.Exit(2)
	}
	if cfg.AppID == "" || cfg.AppSecret == "" || len(cfg.RuntimeNonce) != 64 || len(cfg.IdentityFingerprint) != 64 || len(cfg.IdentityGeneration) != 32 || cfg.ParentPID <= 0 || cfg.ParentStartTimeUTCTicks <= 0 || len(cfg.ParentExecutableFingerprint) != 64 || len(cfg.ParentPayloadManifest) != 64 || len(cfg.SidecarExecutableFingerprint) != 64 {
		fmt.Fprintln(os.Stderr, "startup config is incomplete")
		os.Exit(2)
	}
	if os.Getppid() != cfg.ParentPID {
		fmt.Fprintln(os.Stderr, "parent process identity mismatch")
		os.Exit(2)
	}
	sidecarFingerprint, err := currentExecutableFingerprint()
	if err != nil || !fixedTimeEqual(sidecarFingerprint, cfg.SidecarExecutableFingerprint) {
		fmt.Fprintln(os.Stderr, "sidecar executable attestation failed")
		os.Exit(2)
	}

	apiClient := lark.NewClient(
		cfg.AppID,
		cfg.AppSecret,
		lark.WithOpenBaseUrl("https://open.feishu.cn"),
		lark.WithLogger(discardLogger{}),
		lark.WithHttpClient(resourceHTTPClient{}),
		lark.WithLogLevel(larkcore.LogLevelError),
	)
	botIdentity, identityFailure := queryBotIdentity(context.Background(), apiClient)
	if identityFailure != nil {
		fmt.Fprintln(os.Stderr, "bot identity verification failed")
		os.Exit(2)
	}
	actualIdentityFingerprint := identityFingerprint(
		cfg.AppID,
		botIdentity.Bot.OpenID,
		botIdentity.Bot.AppName,
	)
	if !fixedTimeEqual(actualIdentityFingerprint, cfg.IdentityFingerprint) {
		fmt.Fprintln(os.Stderr, "bot identity generation changed")
		os.Exit(2)
	}
	readyEvent := outputEvent{
		Type:                         "lifecycle",
		State:                        "ready",
		RuntimeNonce:                 cfg.RuntimeNonce,
		IdentityFingerprint:          actualIdentityFingerprint,
		IdentityGeneration:           cfg.IdentityGeneration,
		ParentPID:                    cfg.ParentPID,
		ParentStartTimeUTCTicks:      cfg.ParentStartTimeUTCTicks,
		ParentExecutableFingerprint:  cfg.ParentExecutableFingerprint,
		ParentPayloadManifest:        cfg.ParentPayloadManifest,
		RegisteredConsumers:          append([]string(nil), registeredConsumers...),
		SidecarExecutableFingerprint: sidecarFingerprint,
	}
	sendReply := func(ctx context.Context, messageID, text, idempotencyKey string) error {
		content, err := json.Marshal(struct {
			Text string `json:"text"`
		}{Text: text})
		if err != nil {
			return &replyFailure{stage: "encoding"}
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
		if err != nil {
			return &replyFailure{stage: "transport"}
		}
		if resp == nil {
			return &replyFailure{stage: "transport"}
		}
		if !resp.Success() {
			return &replyFailure{stage: "api", code: resp.Code}
		}
		return nil
	}
	sendP2P := func(ctx context.Context, recipientID, text, idempotencyKey string) error {
		content, err := json.Marshal(struct {
			Text string `json:"text"`
		}{Text: text})
		if err != nil {
			return &replyFailure{stage: "encoding"}
		}
		req := larkim.NewCreateMessageReqBuilder().
			ReceiveIdType("open_id").
			Body(larkim.NewCreateMessageReqBodyBuilder().
				ReceiveId(recipientID).
				Content(string(content)).
				MsgType("text").
				Uuid(idempotencyKey).
				Build()).
			Build()
		resp, err := apiClient.Im.Message.Create(ctx, req)
		if err != nil || resp == nil {
			return &replyFailure{stage: "transport"}
		}
		if !resp.Success() {
			return &replyFailure{stage: "api", code: resp.Code}
		}
		return nil
	}
	sendCard := func(ctx context.Context, recipientID, card, idempotencyKey string) error {
		req := larkim.NewCreateMessageReqBuilder().ReceiveIdType("open_id").
			Body(larkim.NewCreateMessageReqBodyBuilder().ReceiveId(recipientID).Content(card).MsgType("interactive").Uuid(idempotencyKey).Build()).Build()
		resp, err := apiClient.Im.Message.Create(ctx, req)
		if err != nil || resp == nil {
			return &replyFailure{stage: "transport"}
		}
		if !resp.Success() {
			return &replyFailure{stage: "api", code: resp.Code}
		}
		return nil
	}
	menus := newMenuManager(apiClient, cfg)

	handler := dispatcher.NewEventDispatcher("", "").OnP2MessageReceiveV1(
		func(ctx context.Context, event *larkim.P2MessageReceiveV1) error {
			output, ok := normalizeEvent(event)
			if !ok {
				return nil
			}
			return emit(output)
		},
	).OnP2BotMenuV6(func(ctx context.Context, event *larkapplication.P2BotMenuV6) error {
		if event == nil || event.EventV2Base == nil || event.EventV2Base.Header == nil || event.EventV2Base.Header.AppID != cfg.AppID || event.EventV2Base.Header.TenantKey == "" {
			return nil
		}
		output, ok := normalizeMenuEvent(event)
		if !ok {
			return nil
		}
		return emit(output)
	}).OnP2CardActionTrigger(func(ctx context.Context, event *callback.CardActionTriggerEvent) (*callback.CardActionTriggerResponse, error) {
		output, ok := normalizeCardEvent(event)
		if ok {
			_ = emit(output)
		}
		return &callback.CardActionTriggerResponse{}, nil
	})
	wsClient := larkws.NewClient(
		cfg.AppID,
		cfg.AppSecret,
		larkws.WithDomain("https://open.feishu.cn"),
		larkws.WithEventHandler(handler),
		larkws.WithLogger(discardLogger{}),
		larkws.WithLogLevel(larkcore.LogLevelError),
		larkws.WithOnReady(func() {
			_ = emit(readyEvent)
		}),
		larkws.WithOnReconnecting(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "reconnecting"})
		}),
		larkws.WithOnReconnected(func() {
			_ = emit(readyEvent)
		}),
		larkws.WithOnDisconnected(func() {
			_ = emit(outputEvent{Type: "lifecycle", State: "disconnected"})
		}),
	)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	go consumeCommands(ctx, decoder, sendReply, sendP2P, sendCard, menus, stop, newTaskCardTransport(apiClient))
	if err := wsClient.Start(ctx); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, "websocket lifecycle stopped")
		os.Exit(1)
	}
}
