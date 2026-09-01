package main

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	channeltypes "github.com/larksuite/oapi-sdk-go/v3/channel/types"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

func TestReplyCommandUsesSdkInputsAndReturnsOpaqueStatus(t *testing.T) {
	command := inputCommand{
		Type:           "reply",
		RequestID:      "0123456789abcdef0123456789abcdef",
		MessageID:      "sensitive-message-reference",
		Text:           "sensitive reply body",
		IdempotencyKey: "deepori-win-test-opaque-digest",
	}
	called := 0
	result, ok := processReplyCommand(
		context.Background(),
		command,
		func(_ context.Context, messageID, text, idempotencyKey string) error {
			called++
			if messageID != command.MessageID || text != command.Text || idempotencyKey != command.IdempotencyKey {
				t.Fatal("reply sender did not receive the stdin command fields")
			}
			return nil
		},
	)
	if !ok || called != 1 || result.State != "succeeded" || result.RequestID != command.RequestID {
		t.Fatalf("unexpected command result: %#v", result)
	}
	encoded, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	for _, sensitive := range []string{command.MessageID, command.Text, command.IdempotencyKey} {
		if strings.Contains(string(encoded), sensitive) {
			t.Fatal("command result must not echo reply secrets or content")
		}
	}
}

func TestReplyCommandFailureDoesNotEchoSensitiveFields(t *testing.T) {
	command := inputCommand{
		Type:           "reply",
		RequestID:      "fedcba9876543210fedcba9876543210",
		MessageID:      "sensitive-message-reference",
		Text:           "sensitive reply body",
		IdempotencyKey: "opaque-idempotency-key",
	}
	result, ok := processReplyCommand(
		context.Background(),
		command,
		func(context.Context, string, string, string) error { return context.DeadlineExceeded },
	)
	if !ok || result.State != "failed" {
		t.Fatalf("expected a generic failed result: %#v", result)
	}
	encoded, _ := json.Marshal(result)
	for _, sensitive := range []string{command.MessageID, command.Text, command.IdempotencyKey} {
		if strings.Contains(string(encoded), sensitive) {
			t.Fatal("failed command result must not echo sensitive fields")
		}
	}
}

func TestReplyCommandRejectsNonOpaqueRequestIDWithoutEcho(t *testing.T) {
	result, ok := processReplyCommand(
		context.Background(),
		inputCommand{
			Type:           "reply",
			RequestID:      "sensitive-user-controlled-id",
			MessageID:      "message",
			Text:           "reply",
			IdempotencyKey: "key",
		},
		func(context.Context, string, string, string) error { return nil },
	)
	if ok || result != (outputEvent{}) {
		t.Fatal("invalid correlation ids must be ignored without stdout output")
	}
}

func TestNormalizeTextEvent(t *testing.T) {
	event := makeTextEvent("user")

	got, ok := normalizeEvent(event)
	if !ok {
		t.Fatal("expected event to normalize")
	}
	if got.Type != "im.message.receive_v1" || got.ChatType != "p2p" {
		t.Fatalf("unexpected route metadata: %#v", got)
	}
	if got.SenderType != "user" {
		t.Fatalf("expected sender type from event, got %#v", got)
	}
	if got.SenderID != "owner-open-id" || got.MessageID != "message-id" {
		t.Fatalf("unexpected identity metadata: %#v", got)
	}
	if got.MessageType != "text" || got.Content != "Windows Codex：只读测试" {
		t.Fatalf("unexpected normalized content: %#v", got)
	}
}

func TestNormalizePreservesBotSender(t *testing.T) {
	got, ok := normalizeEvent(makeTextEvent("bot"))
	if !ok || got.SenderType != "bot" {
		t.Fatalf("expected bot sender type to remain fail-closed metadata: %#v", got)
	}
}

func TestNormalizeMissingSenderTypeIsEmpty(t *testing.T) {
	got, ok := normalizeEvent(makeTextEvent(""))
	if !ok || got.SenderType != "" {
		t.Fatalf("expected missing sender type to remain empty: %#v", got)
	}
}

func TestNormalizePreservesUnknownSenderType(t *testing.T) {
	got, ok := normalizeEvent(makeTextEvent("service"))
	if !ok || got.SenderType != "service" {
		t.Fatalf("expected unknown sender type to remain fail-closed metadata: %#v", got)
	}
}

func TestNormalizeTextWithoutMentionsIsUnchanged(t *testing.T) {
	const content = "Windows Codex：只读测试"
	got, ok := normalizeEvent(makeTextEventWithMentions("user", content))
	if !ok || got.Content != content {
		t.Fatalf("expected text without mentions to remain unchanged")
	}
}

func TestNormalizeStripsOfficialPrefixMention(t *testing.T) {
	tests := []struct {
		name    string
		content string
	}{
		{name: "separated", content: "@_user_1 Windows Codex 路由测试"},
		{name: "adjacent Chinese text", content: "@_user_1路由测试"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, ok := normalizeEvent(makeTextEventWithMentions("user", test.content, "@_user_1"))
			expected := strings.TrimPrefix(test.content, "@_user_1")
			expected = strings.TrimSpace(expected)
			if !ok || got.Content != expected {
				t.Fatalf("expected the official prefix mention key to be removed")
			}
		})
	}
}

func TestNormalizeStripsMultipleOfficialMentions(t *testing.T) {
	got, ok := normalizeEvent(makeTextEventWithMentions(
		"user",
		"@_user_1 @_user_2 Windows Codex 路由测试",
		"@_user_1",
		"@_user_2",
	))
	if !ok || got.Content != "Windows Codex 路由测试" {
		t.Fatalf("expected all official mention keys to be removed")
	}
}

func TestNormalizeMentionedFixedCommands(t *testing.T) {
	tests := []struct {
		name    string
		content string
	}{
		{name: "fixed route command", content: "Windows Codex 路由测试"},
		{name: "read-only route prefix", content: "Windows Codex：只读测试"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, ok := normalizeEvent(makeTextEventWithMentions(
				"user",
				"@_user_1 "+test.content,
				"@_user_1",
			))
			if !ok || got.Content != test.content {
				t.Fatalf("expected official mention to be removed from %s", test.name)
			}
		})
	}
}

func TestStripOfficialMentionKeysWithNilOrEmptyMentionsIsUnchanged(t *testing.T) {
	const content = "@_user_1 Windows Codex 路由测试"
	for _, mentions := range [][]channeltypes.Mention{nil, {}} {
		if got := stripOfficialMentionKeys(content, mentions); got != content {
			t.Fatal("expected content without official mention metadata to remain unchanged")
		}
	}
}

func TestNormalizeDoesNotStripSimilarOrdinaryText(t *testing.T) {
	const content = "保留 abc@_user_1 和 @_user_10"
	got, ok := normalizeEvent(makeTextEventWithMentions(
		"user",
		content,
		"@_user_1",
	))
	if !ok || got.Content != content {
		t.Fatalf("expected similar ordinary text to remain unchanged")
	}
}

func TestNormalizeOneMentionEntryRemovesAtMostOneOccurrence(t *testing.T) {
	const content = "@_user_1 Windows Codex 路由测试；正文引用 @_user_1"
	got, ok := normalizeEvent(makeTextEventWithMentions(
		"user",
		content,
		"@_user_1",
	))
	if !ok || got.Content != "Windows Codex 路由测试；正文引用 @_user_1" {
		t.Fatal("one official mention entry must remove at most one exact occurrence")
	}
}

func makeTextEvent(senderType string) *larkim.P2MessageReceiveV1 {
	return makeTextEventWithMentions(senderType, "Windows Codex：只读测试")
}

func makeTextEventWithMentions(senderType, content string, mentionKeys ...string) *larkim.P2MessageReceiveV1 {
	sender := larkim.NewEventSenderBuilder().
		SenderId(larkim.NewUserIdBuilder().OpenId("owner-open-id").Build())
	if senderType != "" {
		sender.SenderType(senderType)
	}
	mentions := make([]*larkim.MentionEvent, 0, len(mentionKeys))
	for _, key := range mentionKeys {
		mentions = append(mentions, larkim.NewMentionEventBuilder().Key(key).Build())
	}
	return &larkim.P2MessageReceiveV1{
		Event: &larkim.P2MessageReceiveV1Data{
			Sender: sender.Build(),
			Message: larkim.NewEventMessageBuilder().
				MessageId("message-id").
				ChatType("p2p").
				MessageType("text").
				Content(mustTextContent(content)).
				Mentions(mentions).
				Build(),
		},
	}
}

func mustTextContent(content string) string {
	encoded, err := json.Marshal(map[string]string{"text": content})
	if err != nil {
		panic(err)
	}
	return string(encoded)
}

func TestNormalizeRejectsIncompleteEvent(t *testing.T) {
	if _, ok := normalizeEvent(&larkim.P2MessageReceiveV1{}); ok {
		t.Fatal("expected incomplete event to be ignored")
	}
}
