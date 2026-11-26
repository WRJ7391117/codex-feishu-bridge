package main

import (
	"context"
	"encoding/json"
	"reflect"
	"strings"
	"testing"

	channeltypes "github.com/larksuite/oapi-sdk-go/v3/channel/types"
	larkcore "github.com/larksuite/oapi-sdk-go/v3/core"
	larkevent "github.com/larksuite/oapi-sdk-go/v3/event"
	"github.com/larksuite/oapi-sdk-go/v3/event/dispatcher/callback"
	larkapplication "github.com/larksuite/oapi-sdk-go/v3/service/application/v6"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

func TestRegisteredConsumerManifestIsExact(t *testing.T) {
	expected := []string{
		"im.message.receive_v1",
		"application.bot.menu_v6",
		"card.action.trigger",
	}
	if !reflect.DeepEqual(registeredConsumers, expected) {
		t.Fatalf("registered consumer manifest changed: %#v", registeredConsumers)
	}
}

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

func TestReplyCommandFailureReturnsOnlySafeStageAndCode(t *testing.T) {
	command := inputCommand{
		Type:           "reply",
		RequestID:      "abcdef0123456789abcdef0123456789",
		MessageID:      "sensitive-message-reference",
		Text:           "sensitive reply body",
		IdempotencyKey: "opaque-idempotency-key",
	}
	result, ok := processReplyCommand(
		context.Background(),
		command,
		func(context.Context, string, string, string) error {
			return &replyFailure{stage: "api", code: 230001}
		},
	)
	if !ok || result.State != "failed" || result.ErrorStage != "api" || result.ErrorCode != 230001 {
		t.Fatalf("expected safe API failure metadata: %#v", result)
	}
	encoded, _ := json.Marshal(result)
	for _, sensitive := range []string{command.MessageID, command.Text, command.IdempotencyKey} {
		if strings.Contains(string(encoded), sensitive) {
			t.Fatal("safe failure metadata must not echo sensitive fields")
		}
	}
}

func TestFieldValidationDiagnosticExposesOnlySafeFieldAndRule(t *testing.T) {
	diagnostic := safeFieldValidationDiagnostic(
		99992402,
		"field validation failed; do not echo potentially sensitive upstream text",
		[]*larkcore.CodeErrorFieldViolation{
			{Field: "bot.bot_menus[0].menu_id", Value: "sensitive-value", Description: "length exceeds limit"},
			{Field: "unsafe field with spaces", Value: "secret", Description: "format invalid"},
		},
	)
	if diagnostic != "field_validation_failed;bot.bot_menus[0].menu_id:length" {
		t.Fatalf("unexpected safe validation diagnostic: %q", diagnostic)
	}
	if strings.Contains(diagnostic, "sensitive") || strings.Contains(diagnostic, "secret") {
		t.Fatal("validation diagnostic must never echo upstream values or raw message text")
	}
}

func TestPermissionDiagnosticEmitsOnlyAllowlistedRequiredScopes(t *testing.T) {
	diagnostic := safePermissionDiagnostic(
		99991672,
		"Access denied; token=secret",
		[]*larkcore.CodeErrorDetail{
			{Key: "required_scopes", Value: "application:bot.menu:write,dangerous:unknown_scope"},
		},
		[]*larkcore.CodeErrorPermissionViolation{
			{Subject: "application:application:self_manage", Description: "required"},
		},
	)
	if diagnostic != "required_scope:application:application:self_manage,application:bot.menu:write" {
		t.Fatalf("unexpected safe permission diagnostic: %q", diagnostic)
	}
	for _, unsafe := range []string{"dangerous", "token", "secret"} {
		if strings.Contains(diagnostic, unsafe) {
			t.Fatalf("permission diagnostic leaked non-allowlisted data: %q", diagnostic)
		}
	}
}

func TestMenuPatchSerializationContainsTheCompleteFloatingContract(t *testing.T) {
	encoded, err := json.Marshal(desiredMenuPatchBody(desiredCanonicalMenuNodes(), menuDisplayStrategyFloating, 0, false))
	if err != nil {
		t.Fatal(err)
	}
	var payload struct {
		Bot struct {
			Enable                 *bool            `json:"enable"`
			BotMenuEnable          *bool            `json:"bot_menu_enable"`
			BotMenus               []map[string]any `json:"bot_menus"`
			BotMenuDisplayStrategy *int             `json:"bot_menu_display_strategy"`
		} `json:"bot"`
	}
	if err := json.Unmarshal(encoded, &payload); err != nil {
		t.Fatal(err)
	}
	if payload.Bot.Enable == nil || !*payload.Bot.Enable {
		t.Fatal("v7 application ability PATCH requires bot.enable=true")
	}
	if payload.Bot.BotMenuEnable == nil || !*payload.Bot.BotMenuEnable ||
		payload.Bot.BotMenuDisplayStrategy == nil || *payload.Bot.BotMenuDisplayStrategy != menuDisplayStrategyFloating {
		t.Fatal("menu patch must explicitly enable the floating managed menu")
	}
	if len(payload.Bot.BotMenus) != len(plannedCanonicalMenuNodes()) {
		t.Fatalf("floating menu must contain the complete backed contract: %d", len(payload.Bot.BotMenus))
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
	if ok || !reflect.DeepEqual(result, outputEvent{}) {
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

func TestNormalizeTextUsesRawOfficialContentEnvelope(t *testing.T) {
	event := makeTextEventWithMentions("user", "Windows Bridge 回执测试")
	got, ok := normalizeEvent(event)
	if !ok || got.MessageType != "text" || got.Content != "Windows Bridge 回执测试" || got.ContentState != "text-json-decoded" {
		t.Fatalf("expected exact text from the official content envelope, got %#v", got)
	}
}

func TestNormalizeTextRejectsMalformedOfficialContentEnvelope(t *testing.T) {
	event := makeTextEvent("user")
	malformed := `{"text":`
	event.Event.Message.Content = &malformed
	if _, ok := normalizeEvent(event); ok {
		t.Fatal("malformed official text content must fail closed")
	}
}

func TestNormalizeMenuConsumerCallback(t *testing.T) {
	openID := "menu-user"
	eventKey := "current_task"
	got, ok := normalizeMenuEvent(&larkapplication.P2BotMenuV6{EventV2Base: &larkevent.EventV2Base{Header: &larkevent.EventHeader{EventID: "menu-event-id"}}, Event: &larkapplication.P2BotMenuV6Data{
		Operator: &larkapplication.Operator{OperatorId: &larkapplication.UserId{OpenId: &openID}},
		EventKey: &eventKey,
	}})
	if !ok || got.Type != "application.bot.menu_v6" || got.MessageID != "menu-event-id" || got.SenderID != openID || got.Content != eventKey {
		t.Fatalf("unexpected menu callback: %#v", got)
	}
}

func TestPlannedMenuMatchesMacFourGroupContractAndIsPublishable(t *testing.T) {
	nodes := plannedCanonicalMenuNodes()
	parents := map[string]int{}
	eventKeys := map[string]bool{}
	for _, node := range nodes {
		if node.ParentID == "" {
			parents[node.ID] = 0
			continue
		}
		parents[node.ParentID]++
		eventKeys[node.EventKey] = true
	}
	if len(parents) != 4 {
		t.Fatalf("expected exactly four first-level menus, got %d", len(parents))
	}
	for parent, count := range parents {
		if count == 0 || count > 5 {
			t.Fatalf("parent %s violates the 1..5 child limit: %d", parent, count)
		}
	}
	if len(eventKeys) != 12 || !eventKeys["promlight"] || !eventKeys["promlight_legend"] {
		t.Fatalf("desired menu must expose all twelve event keys: %#v", eventKeys)
	}
	if !knownMenuEventKeys["promlight"] || !knownMenuEventKeys["promlight_legend"] {
		t.Fatal("PromLight keys must remain known and published")
	}
	publishable := desiredCanonicalMenuNodes()
	if len(publishable) != len(nodes) || len(publishableMenuEventKeys) != 12 {
		t.Fatalf("Windows menu contract must publish all backed actions: %#v", publishable)
	}
}

func TestMenuComparisonAndConflictPolicy(t *testing.T) {
	desired := plannedCanonicalMenuNodes()
	if !menuNodesEqual(desired, append([]canonicalMenuNode(nil), desired...)) {
		t.Fatal("exact desired menu must be idempotent")
	}
	reordered := append([]canonicalMenuNode(nil), desired...)
	reordered[0], reordered[len(reordered)-1] = reordered[len(reordered)-1], reordered[0]
	if !menuNodesEqual(reordered, desired) {
		t.Fatal("remote node order must not create a duplicate menu version")
	}
	partial := append([]canonicalMenuNode(nil), desired[:5]...)
	if menuNodesEqual(partial, desired) || hasCustomMenuConflict(partial) {
		t.Fatal("partial managed menu must be repairable, not a custom conflict")
	}
	wrongKnown := append([]canonicalMenuNode(nil), desired...)
	wrongKnown[len(wrongKnown)-1].EventKey = "promlight"
	if !hasCustomMenuConflict(wrongKnown) {
		t.Fatal("a managed ID with a non-canonical key must fail closed")
	}
	unknown := append([]canonicalMenuNode(nil), desired...)
	unknown[len(unknown)-1].EventKey = "tenant_custom_action"
	if !hasCustomMenuConflict(unknown) {
		t.Fatal("unknown custom event key must stop automatic writes")
	}
	redirect := append([]canonicalMenuNode(nil), desired...)
	redirect[len(redirect)-1].HasRedirect = true
	if !hasCustomMenuConflict(redirect) {
		t.Fatal("custom redirect must stop automatic writes")
	}
	customPresentation := append([]canonicalMenuNode(nil), desired...)
	customPresentation[0].HasCustomPresentation = true
	if hasCustomMenuConflict(customPresentation) {
		t.Fatal("Feishu-materialized presentation on an otherwise exact managed menu must remain migratable")
	}
	unknownParent := canonicalMenuNode{ID: "tenant_group", Name: "custom", ContentType: menuContentTypeSubmenu}
	if !hasCustomMenuConflict(append(partial, unknownParent)) {
		t.Fatal("an empty-key custom submenu must stop automatic writes")
	}
	unknownChild := canonicalMenuNode{ID: "tenant_child", ParentID: desired[0].ID, EventKey: "", ContentType: menuContentTypeEvent}
	if !hasCustomMenuConflict(append(partial, unknownChild)) {
		t.Fatal("an unknown child MenuId must stop automatic writes")
	}
	extraGroupAndChild := append(append([]canonicalMenuNode(nil), partial...),
		canonicalMenuNode{ID: "tenant_group", ContentType: menuContentTypeSubmenu},
		canonicalMenuNode{ID: "tenant_child", ParentID: "tenant_group", ContentType: menuContentTypeEvent})
	if !hasCustomMenuConflict(extraGroupAndChild) {
		t.Fatal("extra custom group and child must stop automatic writes")
	}
	duplicate := append(append([]canonicalMenuNode(nil), partial...), partial[0])
	if !hasCustomMenuConflict(duplicate) {
		t.Fatal("duplicate managed MenuId must stop automatic writes")
	}
	changedName := append([]canonicalMenuNode(nil), desired...)
	changedName[4].Name = "tenant renamed item"
	if !hasCustomMenuConflict(changedName) {
		t.Fatal("a renamed known managed child must stop automatic writes")
	}
	changedSort := append([]canonicalMenuNode(nil), desired...)
	changedSort[4].Sort++
	if hasCustomMenuConflict(changedSort) {
		t.Fatal("a reordered known managed child must be normalized by the exact managed rewrite")
	}
	clearedKey := append([]canonicalMenuNode(nil), desired...)
	clearedKey[4].EventKey = ""
	if !hasCustomMenuConflict(clearedKey) {
		t.Fatal("a known managed child with a cleared EventKey must stop automatic writes")
	}
	legacyMVP := []canonicalMenuNode{
		{ID: "opaque-a", Sort: 1, Name: "当前任务", EventKey: "current_task", ContentType: menuContentTypeEvent},
		{ID: "opaque-b", Sort: 2, Name: "订阅状态", EventKey: "task_subscriptions", ContentType: menuContentTypeEvent},
		{ID: "opaque-c", Sort: 3, Name: "同步当前桌面", EventKey: "sync_desktop", ContentType: menuContentTypeEvent},
	}
	if hasCustomMenuConflict(legacyMVP) {
		t.Fatal("the exact Windows 1.0.3 MVP menu must be safely migratable")
	}
	legacyMVP[0].Sort, legacyMVP[1].Sort, legacyMVP[2].Sort = 0, 1, 2
	legacyMVP[0].HasCustomPresentation = true
	if hasCustomMenuConflict(legacyMVP) {
		t.Fatal("the Feishu-normalized zero-based MVP menu must be safely migratable")
	}
	legacyMVP[2].Name = "tenant renamed item"
	if !hasCustomMenuConflict(legacyMVP) {
		t.Fatal("a modified legacy MVP menu must remain a custom conflict")
	}
}

func TestMenuPublishIsIdempotentWithinOneProcess(t *testing.T) {
	generation := "0123456789abcdef0123456789abcdef"
	publishCalls := 0
	manager := &menuManager{
		cfg:               startupConfig{IdentityGeneration: generation},
		candidateReady:    true,
		lastCandidateHash: desiredMenuHash(),
		inspectOverride: func(context.Context) (menuInspection, *replyFailure) {
			return menuInspection{Stage: "written_pending_publish", Diff: "exact", VersionState: "written_pending_publish", HasUnauditVersion: true}, nil
		},
		verifyOverride: func(context.Context) *replyFailure { return nil },
		publishOverride: func(context.Context, menuInspection) (string, string, *replyFailure) {
			publishCalls++
			return "version-id", "1.2.3", nil
		},
	}
	first, _ := manager.publish(context.Background(), generation)
	second, _ := manager.publish(context.Background(), generation)
	if first.State != "succeeded" || second.State != "succeeded" || second.MenuStage != "submitted_pending_review" {
		t.Fatalf("unexpected idempotent results: %#v %#v", first, second)
	}
	if publishCalls != 1 {
		t.Fatalf("replayed menu_publish made %d API calls; expected one", publishCalls)
	}
}

func TestMenuPublishNoOpsWhenRemoteIsAlreadySubmitted(t *testing.T) {
	generation := "0123456789abcdef0123456789abcdef"
	publishCalls := 0
	manager := &menuManager{
		cfg:               startupConfig{IdentityGeneration: generation},
		candidateReady:    true,
		lastCandidateHash: desiredMenuHash(),
		inspectOverride: func(context.Context) (menuInspection, *replyFailure) {
			return menuInspection{Stage: "submitted_pending_review", Diff: "exact", VersionState: "submitted_pending_review"}, nil
		},
		verifyOverride: func(context.Context) *replyFailure { return nil },
		publishOverride: func(context.Context, menuInspection) (string, string, *replyFailure) {
			publishCalls++
			return "", "", nil
		},
	}
	result, _ := manager.publish(context.Background(), generation)
	if result.State != "succeeded" || result.MenuStage != "submitted_pending_review" || publishCalls != 0 {
		t.Fatalf("submitted remote version must make zero publish calls: %#v calls=%d", result, publishCalls)
	}
}

func TestMenuInspectCommandIsProtocolLevelReadOnly(t *testing.T) {
	generation := "0123456789abcdef0123456789abcdef"
	inspectCalls := 0
	writeCalls := 0
	manager := &menuManager{
		cfg: startupConfig{IdentityGeneration: generation},
		inspectOverride: func(context.Context) (menuInspection, *replyFailure) {
			inspectCalls++
			return menuInspection{Stage: "needs_write", Diff: "managed_drift"}, nil
		},
		verifyOverride: func(context.Context) *replyFailure {
			writeCalls++
			return nil
		},
		publishOverride: func(context.Context, menuInspection) (string, string, *replyFailure) {
			writeCalls++
			return "", "", nil
		},
	}
	result, ok := processCommand(context.Background(), inputCommand{
		Type: "menu_inspect", RequestID: generation, ExpectedIdentityGeneration: generation,
	}, nil, nil, nil, manager)
	if !ok || result.State != "succeeded" || result.Operation != "menu_inspect" || inspectCalls != 1 || writeCalls != 0 {
		t.Fatalf("read-only inspect crossed a write boundary: %#v inspect=%d writes=%d", result, inspectCalls, writeCalls)
	}
}

func TestExplicitMenuSetupVerifiesIdentityAndWritesCandidate(t *testing.T) {
	generation := "0123456789abcdef0123456789abcdef"
	verifyCalls := 0
	patchCalls := 0
	manager := &menuManager{
		cfg: startupConfig{IdentityGeneration: generation},
		inspectOverride: func(context.Context) (menuInspection, *replyFailure) {
			return menuInspection{Stage: "needs_write", Diff: "managed_drift"}, nil
		},
		verifyOverride: func(context.Context) *replyFailure {
			verifyCalls++
			return nil
		},
		patchOverride: func(context.Context) *replyFailure {
			patchCalls++
			return nil
		},
	}
	result, _ := manager.inspectAndApply(context.Background(), generation)
	if result.State != "succeeded" || result.MenuStage != "written_pending_publish" || !manager.candidateReady || verifyCalls != 1 || patchCalls != 1 {
		t.Fatalf("explicit setup did not produce one verified candidate write: %#v ready=%v verify=%d patch=%d", result, manager.candidateReady, verifyCalls, patchCalls)
	}
}

func TestNextVersionIsStableAndNonDuplicating(t *testing.T) {
	if got := nextVersion("1.11.12"); got != "1.11.13" {
		t.Fatalf("unexpected semantic version increment: %s", got)
	}
	if got := nextVersion(""); got != "1.0.0" {
		t.Fatalf("unexpected first version: %s", got)
	}
}

func TestP2PCommandDoesNotEchoRecipientOrText(t *testing.T) {
	command := inputCommand{
		Type: "send_p2p", RequestID: "0123456789abcdef0123456789abcdef",
		RecipientID: "sensitive-recipient", Text: "sensitive-menu-reply", IdempotencyKey: "opaque-key",
	}
	result, ok := processP2PCommand(context.Background(), command, func(context.Context, string, string, string) error { return nil })
	if !ok || result.State != "succeeded" {
		t.Fatalf("unexpected P2P result: %#v", result)
	}
	encoded, _ := json.Marshal(result)
	if strings.Contains(string(encoded), command.RecipientID) || strings.Contains(string(encoded), command.Text) {
		t.Fatal("P2P result must not echo recipient or message text")
	}
}

func TestNormalizeCardConsumerCallback(t *testing.T) {
	token := "0123456789abcdef0123456789abcdef"
	got, ok := normalizeCardEvent(&callback.CardActionTriggerEvent{EventV2Base: &larkevent.EventV2Base{Header: &larkevent.EventHeader{EventID: "card-event-id"}}, Event: &callback.CardActionTriggerRequest{
		Operator: &callback.Operator{OpenID: "card-user"},
		Action:   &callback.CallBackAction{Name: "confirm", Value: map[string]interface{}{"deepori_token": token}},
	}})
	if !ok || got.Type != "card.action.trigger" || got.MessageID != "card-event-id" || got.SenderID != "card-user" || got.Content != "confirm" || got.CardToken != token {
		t.Fatalf("unexpected card callback: %#v", got)
	}
}

func TestNormalizeSelectorCallbackUsesOpaqueOptionCapability(t *testing.T) {
	token := "abcdef0123456789abcdef0123456789"
	got, ok := normalizeCardEvent(&callback.CardActionTriggerEvent{EventV2Base: &larkevent.EventV2Base{Header: &larkevent.EventHeader{EventID: "selector-event-id"}}, Event: &callback.CardActionTriggerRequest{
		Operator: &callback.Operator{OpenID: "card-user"},
		Action:   &callback.CallBackAction{Tag: "select_static", Name: "task_selector", Option: token},
	}})
	if !ok || got.Type != "card.action.trigger" || got.MessageID != "selector-event-id" || got.Content != "" || got.CardToken != token {
		t.Fatalf("selector callback must forward only its opaque option capability: %#v", got)
	}
}

func TestNormalizeSelectorCallbackRejectsRawTaskReference(t *testing.T) {
	_, ok := normalizeCardEvent(&callback.CardActionTriggerEvent{EventV2Base: &larkevent.EventV2Base{Header: &larkevent.EventHeader{EventID: "selector-event-id"}}, Event: &callback.CardActionTriggerRequest{
		Operator: &callback.Operator{OpenID: "card-user"},
		Action:   &callback.CallBackAction{Tag: "select_static", Name: "task_selector", Option: "raw-task-id"},
	}})
	if ok {
		t.Fatal("selector callback must reject a raw task reference instead of an opaque capability")
	}
}

func TestNormalizeSecondaryConsumersFailClosedWithoutIdentity(t *testing.T) {
	if _, ok := normalizeMenuEvent(&larkapplication.P2BotMenuV6{}); ok {
		t.Fatal("menu callback without an open id must fail closed")
	}
	if _, ok := normalizeCardEvent(&callback.CardActionTriggerEvent{}); ok {
		t.Fatal("card callback without an open id must fail closed")
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

func TestNormalizeRealFeishuReplyRegressionPayload(t *testing.T) {
	const command = "Windows Bridge 回执测试"
	got, ok := normalizeEvent(makeTextEventWithMentions("user", command))
	if !ok || got.MessageType != "text" || got.Content != command || got.ContentState != "text-json-decoded" {
		t.Fatalf("official Feishu text payload did not produce canonical command text: %#v", got)
	}
}

func TestNormalizePlainTextPostRegressionPayload(t *testing.T) {
	event := makeTextEvent("user")
	post := `{"title":"","content":[[{"tag":"text","text":"Windows Bridge 回执测试","style":["bold"]}]]}`
	event.Event.Message.MessageType = stringPointer("post")
	event.Event.Message.Content = &post

	got, ok := normalizeEvent(event)
	if !ok || got.MessageType != "text" || got.Content != "Windows Bridge 回执测试" || got.ContentState != "post-text-decoded" {
		t.Fatalf("plain Feishu post did not cross the canonical text boundary: %#v", got)
	}
}

func TestNormalizeOfficialLocaleWrappedPostRegressionPayload(t *testing.T) {
	event := makeTextEvent("user")
	post := `{"zh_cn":{"title":"","content":[[{"tag":"text","text":"Windows Bridge 回执测试","style":[]}]]}}`
	event.Event.Message.MessageType = stringPointer("post")
	event.Event.Message.Content = &post

	got, ok := normalizeEvent(event)
	if !ok || got.MessageType != "text" || got.Content != "Windows Bridge 回执测试" || got.ContentState != "post-text-decoded" {
		t.Fatalf("official locale-wrapped Feishu post did not cross the canonical text boundary: %#v", got)
	}
}

func TestNormalizePostRejectsInteractiveOrAmbiguousNodes(t *testing.T) {
	for _, content := range []string{
		`{"title":"","content":[[{"tag":"a","text":"Windows Bridge 回执测试","href":"https://example.invalid"}]]}`,
		`{"title":"","content":[[{"tag":"text","text":"Windows Bridge 回执测试","unknown":true}]]}`,
		`{"zh_cn":{"title":"","content":[[{"tag":"a","text":"Windows Bridge 回执测试","href":"https://example.invalid"}]]}}`,
		`{"zh_cn":{"title":"","content":[[{"tag":"text","text":"Windows Bridge 回执测试"}]]},"en_us":{"title":"","content":[[{"tag":"text","text":"Windows Bridge reply test"}]]}}`,
		`{"ZH_cn":{"title":"","content":[[{"tag":"text","text":"Windows Bridge 回执测试"}]]}}`,
	} {
		event := makeTextEvent("user")
		event.Event.Message.MessageType = stringPointer("post")
		event.Event.Message.Content = &content
		if got, ok := normalizeEvent(event); !ok || got.ResourceError == "" || got.MessageType != "post" || len(got.Resources) != 0 || got.Content != "" {
			t.Fatalf("unsafe post must return a resource error without command text: %#v", got)
		}
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
				ChatId("chat-id").
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

func stringPointer(value string) *string {
	return &value
}

func TestNormalizeRejectsIncompleteEvent(t *testing.T) {
	if _, ok := normalizeEvent(&larkim.P2MessageReceiveV1{}); ok {
		t.Fatal("expected incomplete event to be ignored")
	}
}

func TestIdentityFingerprintBindsAppBotAndDisplayName(t *testing.T) {
	base := identityFingerprint("test-app", "verified-bot", "Verified Bot")
	if len(base) != 64 {
		t.Fatal("identity fingerprint must be SHA-256")
	}
	if !fixedTimeEqual(base, identityFingerprint("test-app", "verified-bot", "Verified Bot")) {
		t.Fatal("same official identity must produce the same fingerprint")
	}
	if fixedTimeEqual(base, identityFingerprint("test-app", "other-bot", "Verified Bot")) {
		t.Fatal("different bot identity must change the fingerprint")
	}
	if fixedTimeEqual(base, identityFingerprint("test-app", "verified-bot", "Renamed Bot")) {
		t.Fatal("different official bot display name must change the generation")
	}
}

func TestCurrentExecutableFingerprintUsesCanonicalExecutable(t *testing.T) {
	fingerprint, err := currentExecutableFingerprint()
	if err != nil || len(fingerprint) != 64 {
		t.Fatalf("expected canonical test executable SHA-256, err=%v", err)
	}
}
