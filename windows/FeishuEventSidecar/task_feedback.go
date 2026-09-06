package main

import (
	"context"
	"encoding/json"
	"errors"

	lark "github.com/larksuite/oapi-sdk-go/v3"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

type taskCardTransport struct {
	resources *resourceTransport
	create    func(context.Context, string, string, string) (string, error)
	patch     func(context.Context, string, string) error
	owned     map[string]bool
}

func newTaskCardTransport(client *lark.Client) *taskCardTransport {
	return &taskCardTransport{
		resources: newResourceTransport(client),
		owned:     make(map[string]bool),
		create: func(ctx context.Context, recipient, card, key string) (string, error) {
			req := larkim.NewCreateMessageReqBuilder().ReceiveIdType("open_id").Body(
				larkim.NewCreateMessageReqBodyBuilder().ReceiveId(recipient).Content(card).MsgType("interactive").Uuid(key).Build()).Build()
			resp, err := client.Im.Message.Create(ctx, req)
			if err != nil || resp == nil {
				return "", &replyFailure{stage: "transport"}
			}
			if !resp.Success() {
				return "", &replyFailure{stage: "api", code: resp.Code}
			}
			if resp.Data == nil || resp.Data.MessageId == nil || *resp.Data.MessageId == "" {
				return "", &replyFailure{stage: "encoding"}
			}
			return *resp.Data.MessageId, nil
		},
		patch: func(ctx context.Context, id, card string) error {
			req := larkim.NewPatchMessageReqBuilder().MessageId(id).Body(larkim.NewPatchMessageReqBodyBuilder().Content(card).Build()).Build()
			resp, err := client.Im.Message.Patch(ctx, req)
			if err != nil || resp == nil {
				return &replyFailure{stage: "transport"}
			}
			if !resp.Success() {
				return &replyFailure{stage: "api", code: resp.Code}
			}
			return nil
		},
	}
}

func (transport *taskCardTransport) process(ctx context.Context, command inputCommand) (outputEvent, bool) {
	if command.Type == "resource_download" || command.Type == "resource_send" {
		return transport.resources.process(ctx, command)
	}
	if command.Type != "task_card_create" && command.Type != "task_card_patch" {
		return outputEvent{}, false
	}
	if !validOpaqueRequestID(command.RequestID) {
		return outputEvent{}, false
	}
	result := outputEvent{Type: "command_result", RequestID: command.RequestID, Operation: command.Type, State: "failed"}
	if command.Card == "" || len(command.Card) > 12000 || !json.Valid([]byte(command.Card)) {
		return result, true
	}
	var err error
	if command.Type == "task_card_create" {
		if command.RecipientID == "" || len(command.RecipientID) > 256 || command.IdempotencyKey == "" || len(command.IdempotencyKey) > 64 {
			return result, true
		}
		var id string
		id, err = transport.create(ctx, command.RecipientID, command.Card, command.IdempotencyKey)
		if err == nil && id != "" {
			transport.owned[id] = true
			result.MessageID = id
		} else if err == nil {
			err = &replyFailure{stage: "encoding"}
		}
	} else {
		// Only cards created by this exact sidecar session can be patched.
		if !transport.owned[command.MessageID] {
			return result, true
		}
		err = transport.patch(ctx, command.MessageID, command.Card)
	}
	if err != nil {
		var failure *replyFailure
		if errors.As(err, &failure) {
			result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		}
		return result, true
	}
	result.State = "succeeded"
	return result, true
}
