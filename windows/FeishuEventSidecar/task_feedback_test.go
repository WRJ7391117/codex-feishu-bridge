package main

import (
	"context"
	"strings"
	"testing"
)

func TestTaskFeedbackCardLifecycle(t *testing.T) {
	creates, patches := 0, 0
	transport := &taskCardTransport{
		owned: make(map[string]bool),
		create: func(ctx context.Context, recipient, card, key string) (string, error) {
			creates++
			return "owned-card", nil
		},
		patch: func(ctx context.Context, id, card string) error { patches++; return nil },
	}
	command := inputCommand{Type: "task_card_patch", RequestID: strings.Repeat("a", 32), MessageID: "other-card", Card: `{"schema":"2.0"}`}
	result, ok := transport.process(context.Background(), command)
	if !ok || result.State != "failed" || patches != 0 {
		t.Fatal("must reject patching a card not created by this session")
	}
	command.Type = "task_card_create"
	command.RecipientID = "bound-owner"
	command.IdempotencyKey = "stable-key"
	result, ok = transport.process(context.Background(), command)
	if !ok || result.State != "succeeded" || result.MessageID != "owned-card" || creates != 1 {
		t.Fatal("create must return the actual Feishu message ID")
	}
	command.Type = "task_card_patch"
	command.MessageID = result.MessageID
	result, ok = transport.process(context.Background(), command)
	if !ok || result.State != "succeeded" || patches != 1 {
		t.Fatal("running and terminal states update the same owned card")
	}
	command.Card = "{broken"
	result, _ = transport.process(context.Background(), command)
	if result.State != "failed" || patches != 1 {
		t.Fatal("malformed card must not reach the API")
	}
	command.Card = strings.Repeat("x", 12001)
	result, _ = transport.process(context.Background(), command)
	if result.State != "failed" || patches != 1 {
		t.Fatal("oversized card must not reach the API")
	}
}
