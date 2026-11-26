package main

import (
	"context"
	"fmt"
	"reflect"
	"sort"
	"strconv"
	"strings"
	"sync"

	lark "github.com/larksuite/oapi-sdk-go/v3"
	larkcore "github.com/larksuite/oapi-sdk-go/v3/core"
	larkapplicationv6 "github.com/larksuite/oapi-sdk-go/v3/service/application/v6"
	larkapplicationv7 "github.com/larksuite/oapi-sdk-go/v3/service/application/v7"
)

const (
	menuDisplayStrategyFloating = 1
	menuContentTypeEvent        = 2
	menuContentTypeSubmenu      = 3
)

var knownMenuEventKeys = map[string]bool{
	"current_task":         true,
	"select_task":          true,
	"new_task":             true,
	"archive_task":         true,
	"task_subscriptions":   true,
	"sync_desktop":         true,
	"sync_desktop_switch":  true,
	"task_settings":        true,
	"compact_task_context": true,
	"codex_usage":          true,
	"promlight":            true,
	"promlight_legend":     true,
}

// These event keys are backed by Windows card builders and callback handlers.
// Ordinary launch remains read-only; a menu write still requires the explicit
// settings-page operation and the existing identity-generation checks.
var publishableMenuEventKeys = map[string]bool{
	"current_task":         true,
	"select_task":          true,
	"new_task":             true,
	"archive_task":         true,
	"task_subscriptions":   true,
	"sync_desktop":         true,
	"sync_desktop_switch":  true,
	"task_settings":        true,
	"compact_task_context": true,
	"codex_usage":          true,
	"promlight":            true,
	"promlight_legend":     true,
}

type canonicalMenuNode struct {
	ID                    string
	ParentID              string
	Sort                  int
	Name                  string
	EventKey              string
	ContentType           int
	HasRedirect           bool
	HasCustomPresentation bool
}

type menuInspection struct {
	Stage             string
	Diff              string
	VersionState      string
	Version           string
	VersionID         string
	HasUnauditVersion bool
	Nodes             []canonicalMenuNode
	Enabled           bool
	DisplayStrategy   int
}

type menuManager struct {
	client             *lark.Client
	cfg                startupConfig
	mu                 sync.Mutex
	candidateReady     bool
	lastVersionID      string
	lastCandidateHash  string
	patchVariant       string
	submissionRecorded bool
	inspectOverride    func(context.Context) (menuInspection, *replyFailure)
	verifyOverride     func(context.Context) *replyFailure
	publishOverride    func(context.Context, menuInspection) (string, string, *replyFailure)
	patchOverride      func(context.Context) *replyFailure
}

func newMenuManager(client *lark.Client, cfg startupConfig) *menuManager {
	return &menuManager{
		client:            client,
		cfg:               cfg,
		lastCandidateHash: desiredMenuHash(),
	}
}

func desiredMenuHash() string {
	nodes := desiredCanonicalMenuNodes()
	parts := make([]string, 0, len(nodes))
	for _, node := range nodes {
		parts = append(parts, fmt.Sprintf("%s\x00%s\x00%d\x00%s\x00%s\x00%d", node.ID, node.ParentID, node.Sort, node.Name, node.EventKey, node.ContentType))
	}
	return identityFingerprint("DeepOriBridge.BotMenu.v1", strings.Join(parts, "\x00"), strconv.Itoa(menuDisplayStrategyFloating))
}

func plannedCanonicalMenuNodes() []canonicalMenuNode {
	return []canonicalMenuNode{
		{ID: "menu_t", Sort: 0, Name: "Task 管理", ContentType: menuContentTypeSubmenu},
		{ID: "menu_t1", ParentID: "menu_t", Sort: 0, Name: "当前 Task", EventKey: "current_task", ContentType: menuContentTypeEvent},
		{ID: "menu_t2", ParentID: "menu_t", Sort: 1, Name: "切换 Task", EventKey: "select_task", ContentType: menuContentTypeEvent},
		{ID: "menu_t3", ParentID: "menu_t", Sort: 2, Name: "新建 Task", EventKey: "new_task", ContentType: menuContentTypeEvent},
		{ID: "menu_t4", ParentID: "menu_t", Sort: 3, Name: "归档当前 Task", EventKey: "archive_task", ContentType: menuContentTypeEvent},
		{ID: "menu_d", Sort: 1, Name: "桌面 Task", ContentType: menuContentTypeSubmenu},
		{ID: "menu_d1", ParentID: "menu_d", Sort: 0, Name: "订阅桌面 Task", EventKey: "task_subscriptions", ContentType: menuContentTypeEvent},
		{ID: "menu_d2", ParentID: "menu_d", Sort: 1, Name: "接续当前 Task", EventKey: "sync_desktop", ContentType: menuContentTypeEvent},
		{ID: "menu_d3", ParentID: "menu_d", Sort: 2, Name: "接续其他 Task", EventKey: "sync_desktop_switch", ContentType: menuContentTypeEvent},
		{ID: "menu_m", Sort: 2, Name: "模型设置", ContentType: menuContentTypeSubmenu},
		{ID: "menu_m1", ParentID: "menu_m", Sort: 0, Name: "修改当前 Task 模型", EventKey: "task_settings", ContentType: menuContentTypeEvent},
		{ID: "menu_m2", ParentID: "menu_m", Sort: 1, Name: "压缩当前 Task 上下文", EventKey: "compact_task_context", ContentType: menuContentTypeEvent},
		{ID: "menu_m3", ParentID: "menu_m", Sort: 2, Name: "Codex 额度用量", EventKey: "codex_usage", ContentType: menuContentTypeEvent},
		{ID: "menu_p", Sort: 3, Name: "提示灯", ContentType: menuContentTypeSubmenu},
		{ID: "menu_p1", ParentID: "menu_p", Sort: 0, Name: "我的提示灯", EventKey: "promlight", ContentType: menuContentTypeEvent},
		{ID: "menu_p2", ParentID: "menu_p", Sort: 1, Name: "灯光状态说明", EventKey: "promlight_legend", ContentType: menuContentTypeEvent},
	}
}

func desiredCanonicalMenuNodes() []canonicalMenuNode {
	planned := plannedCanonicalMenuNodes()
	parents := make(map[string]bool)
	for _, node := range planned {
		if node.EventKey != "" && publishableMenuEventKeys[node.EventKey] {
			parents[node.ParentID] = true
		}
	}
	result := make([]canonicalMenuNode, 0, len(planned))
	for _, node := range planned {
		if (node.EventKey == "" && parents[node.ID]) || publishableMenuEventKeys[node.EventKey] {
			result = append(result, node)
		}
	}
	return result
}

func desiredBotMenuNodes(canonical []canonicalMenuNode, sortOffset int, globalSort bool) []*larkapplicationv7.BotMenuNode {
	nodes := make([]*larkapplicationv7.BotMenuNode, 0, len(canonical))
	for index, node := range canonical {
		sortValue := node.Sort + sortOffset
		if globalSort {
			sortValue = index + sortOffset
		}
		builder := larkapplicationv7.NewBotMenuNodeBuilder().
			MenuId(node.ID).
			Sort(sortValue).
			DefaultName(node.Name).
			MenuContentType(node.ContentType)
		if node.ParentID != "" {
			builder.ParentMenuId(node.ParentID)
		}
		if node.EventKey != "" {
			builder.EventKey(node.EventKey)
		}
		nodes = append(nodes, builder.Build())
	}
	return nodes
}

func desiredMenuPatchBody(canonical []canonicalMenuNode, displayStrategy int, sortOffset int, globalSort bool) *larkapplicationv7.PatchApplicationAbilityReqBody {
	return larkapplicationv7.NewPatchApplicationAbilityReqBodyBuilder().
		Bot(larkapplicationv7.NewAppAbilityBotBuilder().
			Enable(true).
			BotMenuEnable(true).
			BotMenuDisplayStrategy(displayStrategy).
			BotMenus(desiredBotMenuNodes(canonical, sortOffset, globalSort)).
			Build()).
		Build()
}

func canonicalizeV6Nodes(nodes []*larkapplicationv6.BotMenuNode) []canonicalMenuNode {
	result := make([]canonicalMenuNode, 0, len(nodes))
	for _, node := range nodes {
		if node == nil {
			continue
		}
		item := canonicalMenuNode{}
		if node.MenuId != nil {
			item.ID = *node.MenuId
		}
		if node.ParentMenuId != nil {
			item.ParentID = *node.ParentMenuId
		}
		if node.Sort != nil {
			item.Sort = *node.Sort
		}
		if node.DefaultName != nil {
			item.Name = *node.DefaultName
		}
		if node.EventKey != nil {
			item.EventKey = *node.EventKey
		}
		if node.MenuContentType != nil {
			item.ContentType = *node.MenuContentType
		}
		item.HasRedirect = node.RedirectLink != nil
		item.HasCustomPresentation = len(node.I18nName) != 0 || node.IconFileKey != nil || node.UdIcon != nil
		result = append(result, item)
	}
	return result
}

func menuNodesEqual(actual, desired []canonicalMenuNode) bool {
	if len(actual) != len(desired) {
		return false
	}
	return reflect.DeepEqual(menuSemanticShape(actual), menuSemanticShape(desired))
}

func hasCustomMenuConflict(nodes []canonicalMenuNode) bool {
	// Feishu may replace client-supplied MenuIds with opaque values. Classify
	// ownership by the complete visible parent/leaf contract, never by those IDs.
	// Old Windows previews used three root actions or merged the last two Mac
	// groups. Those exact DeepOri-only shapes are safe to migrate. Any unknown
	// key/name, redirect, custom presentation, duplicate, or orphan fails closed.
	if isLegacyWindowsMVPMenu(nodes) {
		return false
	}
	return !isKnownDeepOriManagedMenu(nodes)
}

type semanticMenuNode struct {
	ParentName            string
	Sort                  int
	Name                  string
	EventKey              string
	ContentType           int
	HasRedirect           bool
	HasCustomPresentation bool
}

func menuSemanticShape(nodes []canonicalMenuNode) []semanticMenuNode {
	parents := make(map[string]string)
	for _, node := range nodes {
		if node.ParentID == "" && node.EventKey == "" {
			parents[node.ID] = node.Name
		}
	}
	shape := make([]semanticMenuNode, 0, len(nodes))
	for _, node := range nodes {
		shape = append(shape, semanticMenuNode{
			ParentName:            parents[node.ParentID],
			Sort:                  node.Sort,
			Name:                  node.Name,
			EventKey:              node.EventKey,
			ContentType:           node.ContentType,
			HasRedirect:           node.HasRedirect,
			HasCustomPresentation: node.HasCustomPresentation,
		})
	}
	sort.Slice(shape, func(i, j int) bool {
		left := fmt.Sprintf("%s\x00%06d\x00%s\x00%s", shape[i].ParentName, shape[i].Sort, shape[i].Name, shape[i].EventKey)
		right := fmt.Sprintf("%s\x00%06d\x00%s\x00%s", shape[j].ParentName, shape[j].Sort, shape[j].Name, shape[j].EventKey)
		return left < right
	})
	return shape
}

func isKnownDeepOriManagedMenu(nodes []canonicalMenuNode) bool {
	if len(nodes) == 0 {
		return true
	}
	knownLeafNames := make(map[string]string)
	for _, node := range plannedCanonicalMenuNodes() {
		if node.EventKey != "" {
			knownLeafNames[node.EventKey] = node.Name
		}
	}
	knownParentNames := map[string]bool{
		"Task 管理": true, "桌面 Task": true, "桌面task": true,
		"模型设置": true, "提示灯": true, "模型与提示灯": true,
	}
	ids := make(map[string]bool)
	parentNames := make(map[string]string)
	seenKeys := make(map[string]bool)
	for _, node := range nodes {
		if node.ID == "" || ids[node.ID] || node.HasRedirect {
			return false
		}
		ids[node.ID] = true
		if node.EventKey == "" {
			if node.ParentID != "" || node.ContentType != menuContentTypeSubmenu || !knownParentNames[node.Name] {
				return false
			}
			parentNames[node.ID] = node.Name
			continue
		}
		if node.ContentType != menuContentTypeEvent || seenKeys[node.EventKey] || knownLeafNames[node.EventKey] != node.Name {
			return false
		}
		seenKeys[node.EventKey] = true
	}
	leafContract := map[string]struct {
		parents map[string]bool
		sorts   map[int]bool
	}{
		"current_task":         {map[string]bool{"Task 管理": true}, map[int]bool{1: true}},
		"select_task":          {map[string]bool{"Task 管理": true}, map[int]bool{2: true}},
		"new_task":             {map[string]bool{"Task 管理": true}, map[int]bool{3: true}},
		"archive_task":         {map[string]bool{"Task 管理": true}, map[int]bool{4: true}},
		"task_subscriptions":   {map[string]bool{"桌面 Task": true, "桌面task": true}, map[int]bool{1: true}},
		"sync_desktop":         {map[string]bool{"桌面 Task": true, "桌面task": true}, map[int]bool{2: true}},
		"sync_desktop_switch":  {map[string]bool{"桌面 Task": true, "桌面task": true}, map[int]bool{3: true}},
		"task_settings":        {map[string]bool{"模型设置": true, "模型与提示灯": true}, map[int]bool{1: true}},
		"compact_task_context": {map[string]bool{"模型设置": true, "模型与提示灯": true}, map[int]bool{2: true}},
		"codex_usage":          {map[string]bool{"模型设置": true, "模型与提示灯": true}, map[int]bool{3: true}},
		"promlight":            {map[string]bool{"提示灯": true, "模型与提示灯": true}, map[int]bool{1: true, 4: true}},
		"promlight_legend":     {map[string]bool{"提示灯": true, "模型与提示灯": true}, map[int]bool{2: true, 5: true}},
	}
	for _, node := range nodes {
		if node.EventKey == "" {
			continue
		}
		contract := leafContract[node.EventKey]
		parentName, hasParent := parentNames[node.ParentID]
		if !hasParent || !contract.parents[parentName] {
			return false
		}
	}
	return true
}

func isLegacyWindowsMVPMenu(nodes []canonicalMenuNode) bool {
	if len(nodes) != 3 {
		return false
	}
	expected := []struct {
		key  string
		name string
	}{
		{key: "current_task", name: "当前任务"},
		{key: "task_subscriptions", name: "订阅状态"},
		{key: "sync_desktop", name: "同步当前桌面"},
	}
	seenIDs := make(map[string]bool)
	zeroBasedSort := nodes[0].Sort == 0
	for index, node := range nodes {
		want := expected[index]
		wantSort := index + 1
		if zeroBasedSort {
			wantSort = index
		}
		if node.ID == "" || seenIDs[node.ID] || node.EventKey != want.key ||
			node.ParentID != "" || node.Name != want.name || node.Sort != wantSort ||
			node.ContentType != menuContentTypeEvent || node.HasRedirect {
			return false
		}
		seenIDs[node.ID] = true
	}
	return true
}

func (m *menuManager) inspectCurrent(ctx context.Context) (menuInspection, *replyFailure) {
	if m.inspectOverride != nil {
		return m.inspectOverride(ctx)
	}
	return m.inspect(ctx)
}

func (m *menuManager) verifyCurrentIdentity(ctx context.Context) *replyFailure {
	if m.verifyOverride != nil {
		return m.verifyOverride(ctx)
	}
	return verifyRuntimeIdentity(ctx, m.client, m.cfg)
}

func (m *menuManager) patchMenu(ctx context.Context, actual []canonicalMenuNode) *replyFailure {
	if m.patchOverride != nil {
		return m.patchOverride(ctx)
	}
	// Send one deterministic candidate. Retrying different payload shapes inside
	// the 20-second sidecar command window hid Feishu's real validation response
	// behind a timeout and could never be a safe migration strategy.
	candidate := migrationCanonicalMenuNodes(actual)
	body := desiredMenuPatchBody(candidate, menuDisplayStrategyFloating, 1, false)
	patchReq := larkapplicationv7.NewPatchApplicationAbilityReqBuilder().AppId(m.cfg.AppID).Body(body).Build()
	patchResp, err := m.client.Application.V7.ApplicationAbility.Patch(ctx, patchReq)
	if err != nil || patchResp == nil {
		return &replyFailure{stage: "transport"}
	}
	if patchResp.Success() {
		m.patchVariant = "managed_ids_floating_one_based"
		return nil
	}
	var violations []*larkcore.CodeErrorFieldViolation
	var details []*larkcore.CodeErrorDetail
	var permissionViolations []*larkcore.CodeErrorPermissionViolation
	if patchResp.Err != nil {
		violations = patchResp.Err.FieldViolations
		details = patchResp.Err.Details
		permissionViolations = patchResp.Err.PermissionViolations
	}
	diagnostic := safeFieldValidationDiagnostic(patchResp.Code, patchResp.Msg, violations)
	if permission := safePermissionDiagnostic(patchResp.Code, patchResp.Msg, details, permissionViolations); permission != "" {
		diagnostic = permission
	}
	failure := &replyFailure{
		stage:      "api",
		code:       patchResp.Code,
		diagnostic: diagnostic,
	}
	if patchResp.ApiResp != nil && diagnostic == "field_validation_failed" {
		if rawDiagnostic := safeRawFieldValidationDiagnostic(
			patchResp.ApiResp.RawBody,
			patchResp.Code,
			patchResp.Msg); rawDiagnostic != "" {
			failure.diagnostic = rawDiagnostic
		}
	}
	return failure
}

func migrationCanonicalMenuNodes(actual []canonicalMenuNode) []canonicalMenuNode {
	planned := plannedCanonicalMenuNodes()
	actualByKey := make(map[string]canonicalMenuNode)
	usedIDs := make(map[string]bool)
	for _, node := range actual {
		usedIDs[node.ID] = true
		if node.EventKey != "" {
			actualByKey[node.EventKey] = node
		}
	}
	groupKeys := map[string]string{
		"menu_t": "current_task",
		"menu_d": "task_subscriptions",
		"menu_m": "task_settings",
		"menu_p": "promlight",
	}
	parentIDs := make(map[string]string)
	claimedParents := make(map[string]bool)
	for groupID, eventKey := range groupKeys {
		if leaf, ok := actualByKey[eventKey]; ok && leaf.ParentID != "" && !claimedParents[leaf.ParentID] {
			parentIDs[groupID] = leaf.ParentID
			claimedParents[leaf.ParentID] = true
			continue
		}
		candidate := groupID
		for suffix := 1; usedIDs[candidate] || claimedParents[candidate]; suffix++ {
			candidate = fmt.Sprintf("%s_%d", groupID, suffix)
		}
		parentIDs[groupID] = candidate
		claimedParents[candidate] = true
		usedIDs[candidate] = true
	}
	result := make([]canonicalMenuNode, 0, len(planned))
	for _, node := range planned {
		if node.EventKey == "" {
			node.ID = parentIDs[node.ID]
		} else {
			originalParent := node.ParentID
			if existing, ok := actualByKey[node.EventKey]; ok && existing.ID != "" {
				node.ID = existing.ID
			}
			node.ParentID = parentIDs[originalParent]
		}
		result = append(result, node)
	}
	return result
}

func versionState(status int) string {
	switch status {
	case larkapplicationv6.AppVersionStatusAudited:
		return "published"
	case larkapplicationv6.AppVersionStatusReject:
		return "rejected"
	case larkapplicationv6.AppVersionStatusUnderAudit:
		return "submitted_pending_review"
	case larkapplicationv6.AppVersionStatusUnaudit:
		return "written_pending_publish"
	default:
		return "unknown"
	}
}

func stageForVersion(state string, isOnline bool) string {
	switch state {
	case "published":
		if isOnline {
			return "published_pending_visibility"
		}
		return "submitted_pending_review"
	case "rejected":
		return "publish_rejected"
	case "submitted_pending_review":
		return "submitted_pending_review"
	case "written_pending_publish":
		return "written_pending_publish"
	default:
		return "written_pending_publish"
	}
}

func (m *menuManager) inspect(ctx context.Context) (menuInspection, *replyFailure) {
	appReq := larkapplicationv6.NewGetApplicationReqBuilder().AppId(m.cfg.AppID).Lang("zh_cn").Build()
	appResp, err := m.client.Application.Application.Get(ctx, appReq)
	if err != nil || appResp == nil {
		return menuInspection{}, &replyFailure{stage: "transport"}
	}
	if !appResp.Success() {
		return menuInspection{}, &replyFailure{stage: "api", code: appResp.Code}
	}
	if appResp.Data == nil || appResp.Data.App == nil || appResp.Data.App.AppId == nil || *appResp.Data.App.AppId != m.cfg.AppID {
		return menuInspection{}, &replyFailure{stage: "identity"}
	}

	app := appResp.Data.App
	versionID := ""
	hasUnaudit := app.UnauditVersionId != nil && strings.TrimSpace(*app.UnauditVersionId) != ""
	if hasUnaudit {
		versionID = strings.TrimSpace(*app.UnauditVersionId)
	} else if app.OnlineVersionId != nil {
		versionID = strings.TrimSpace(*app.OnlineVersionId)
	}
	if versionID == "" {
		return menuInspection{Stage: "needs_write", Diff: "empty", VersionState: "none"}, nil
	}

	versionReq := larkapplicationv6.NewGetApplicationAppVersionReqBuilder().
		AppId(m.cfg.AppID).
		VersionId(versionID).
		Lang("zh_cn").
		Build()
	versionResp, err := m.client.Application.ApplicationAppVersion.Get(ctx, versionReq)
	if err != nil || versionResp == nil {
		return menuInspection{}, &replyFailure{stage: "transport"}
	}
	if !versionResp.Success() {
		return menuInspection{}, &replyFailure{stage: "api", code: versionResp.Code}
	}
	if versionResp.Data == nil || versionResp.Data.AppVersion == nil || versionResp.Data.AppVersion.AppId == nil || *versionResp.Data.AppVersion.AppId != m.cfg.AppID {
		return menuInspection{}, &replyFailure{stage: "identity"}
	}

	version := versionResp.Data.AppVersion
	inspection := menuInspection{
		Stage:             "needs_write",
		Diff:              "managed_drift",
		VersionID:         versionID,
		HasUnauditVersion: hasUnaudit,
	}
	if version.Version != nil {
		inspection.Version = *version.Version
	}
	status := 0
	if version.Status != nil {
		status = *version.Status
	}
	inspection.VersionState = versionState(status)
	if version.Ability != nil && version.Ability.Bot != nil {
		bot := version.Ability.Bot
		if bot.BotMenuEnable != nil {
			inspection.Enabled = *bot.BotMenuEnable
		}
		if bot.BotMenuDisplayStrategy != nil {
			inspection.DisplayStrategy = *bot.BotMenuDisplayStrategy
		}
		inspection.Nodes = canonicalizeV6Nodes(bot.BotMenus)
	}
	if hasCustomMenuConflict(inspection.Nodes) {
		inspection.Stage = "custom_conflict"
		inspection.Diff = "custom_conflict"
		return inspection, nil
	}
	if inspection.Enabled && inspection.DisplayStrategy == menuDisplayStrategyFloating && menuNodesEqual(inspection.Nodes, desiredCanonicalMenuNodes()) {
		inspection.Diff = "exact"
		inspection.Stage = stageForVersion(inspection.VersionState, !hasUnaudit)
	}
	return inspection, nil
}

func (m *menuManager) inspectAndApply(ctx context.Context, expectedGeneration string) (outputEvent, bool) {
	result := outputEvent{Type: "command_result", Operation: "menu_inspect_apply", State: "failed"}
	if !fixedTimeEqual(expectedGeneration, m.cfg.IdentityGeneration) {
		result.ErrorStage = "identity"
		return result, true
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	inspection, failure := m.inspectCurrent(ctx)
	if failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	if inspection.Stage == "custom_conflict" {
		result.State = "succeeded"
		result.MenuStage = inspection.Stage
		result.MenuDiff = inspection.Diff
		result.VersionState = inspection.VersionState
		return result, true
	}
	if len(desiredCanonicalMenuNodes()) == 0 {
		m.candidateReady = false
		result.State = "succeeded"
		result.MenuStage = "capability_gate"
		result.MenuDiff = "mac_parity_pending"
		result.VersionState = inspection.VersionState
		result.Version = inspection.Version
		return result, true
	}
	if inspection.Diff == "exact" {
		m.candidateReady = inspection.Stage != "published_pending_visibility"
		m.lastVersionID = inspection.VersionID
		result.State = "succeeded"
		result.MenuStage = inspection.Stage
		result.MenuDiff = inspection.Diff
		result.VersionState = inspection.VersionState
		result.Version = inspection.Version
		return result, true
	}
	if failure = m.verifyCurrentIdentity(ctx); failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	if failure = m.patchMenu(ctx, inspection.Nodes); failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		parentCount := 0
		for _, node := range inspection.Nodes {
			if node.ParentID == "" && node.EventKey == "" {
				parentCount++
			}
		}
		result.ErrorDiagnostic = fmt.Sprintf(
			"%s;remote_display:%d;remote_nodes:%d;remote_parents:%d",
			failure.diagnostic,
			inspection.DisplayStrategy,
			len(inspection.Nodes),
			parentCount)
		return result, true
	}
	m.candidateReady = true
	m.lastVersionID = inspection.VersionID
	result.State = "succeeded"
	result.MenuStage = "written_pending_publish"
	result.MenuDiff = inspection.Diff + "_" + m.patchVariant
	result.VersionState = "written_pending_publish"
	return result, true
}

func (m *menuManager) publish(ctx context.Context, expectedGeneration string) (outputEvent, bool) {
	result := outputEvent{Type: "command_result", Operation: "menu_publish", State: "failed"}
	if !fixedTimeEqual(expectedGeneration, m.cfg.IdentityGeneration) {
		result.ErrorStage = "identity"
		return result, true
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.candidateReady || !fixedTimeEqual(m.lastCandidateHash, desiredMenuHash()) {
		result.ErrorStage = "candidate"
		return result, true
	}
	if m.submissionRecorded {
		result.State = "succeeded"
		result.MenuStage = "submitted_pending_review"
		result.MenuDiff = "exact"
		result.VersionState = "submitted_pending_review"
		return result, true
	}
	inspection, failure := m.inspectCurrent(ctx)
	if failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	if inspection.Stage == "custom_conflict" {
		result.State = "succeeded"
		result.MenuStage = inspection.Stage
		result.MenuDiff = inspection.Diff
		result.VersionState = inspection.VersionState
		return result, true
	}
	if inspection.Stage == "published_pending_visibility" {
		m.submissionRecorded = true
		result.State = "succeeded"
		result.MenuStage = inspection.Stage
		result.MenuDiff = "exact"
		result.VersionState = inspection.VersionState
		return result, true
	}
	if inspection.Stage == "submitted_pending_review" {
		m.submissionRecorded = true
		result.State = "succeeded"
		result.MenuStage = inspection.Stage
		result.MenuDiff = "exact"
		result.VersionState = inspection.VersionState
		result.Version = inspection.Version
		return result, true
	}
	if failure = m.verifyCurrentIdentity(ctx); failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	versionID, version, failure := m.callPublish(ctx, inspection)
	if failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	m.submissionRecorded = true
	m.lastVersionID = versionID
	result.State = "succeeded"
	result.MenuStage = "submitted_pending_review"
	result.MenuDiff = "exact_" + m.patchVariant
	result.VersionState = "submitted_pending_review"
	result.Version = version
	return result, true
}

func (m *menuManager) callPublish(ctx context.Context, inspection menuInspection) (string, string, *replyFailure) {
	if m.publishOverride != nil {
		return m.publishOverride(ctx, inspection)
	}
	bodyBuilder := larkapplicationv7.NewCreateApplicationPublishReqBodyBuilder().
		MobileDefaultAbility(larkapplicationv7.CreateApplicationPublishMobileDefaultAbilityBot).
		PcDefaultAbility(larkapplicationv7.CreateApplicationPublishPcDefaultAbilityBot).
		Remark("DeepOri Bridge for Windows 受管 Bot 菜单").
		Changelog("配置 Windows Bridge Bot 菜单与回调入口")
	if !inspection.HasUnauditVersion {
		bodyBuilder.Version(nextVersion(inspection.Version))
	}
	publishReq := larkapplicationv7.NewCreateApplicationPublishReqBuilder().AppId(m.cfg.AppID).Body(bodyBuilder.Build()).Build()
	publishResp, err := m.client.Application.V7.ApplicationPublish.Create(ctx, publishReq)
	if err != nil || publishResp == nil {
		return "", "", &replyFailure{stage: "transport"}
	}
	if !publishResp.Success() {
		return "", "", &replyFailure{stage: "api", code: publishResp.Code}
	}
	versionID := ""
	version := ""
	if publishResp.Data != nil {
		if publishResp.Data.VersionId != nil {
			versionID = *publishResp.Data.VersionId
		}
		if publishResp.Data.Version != nil {
			version = *publishResp.Data.Version
		}
	}
	return versionID, version, nil
}

func (m *menuManager) status(ctx context.Context, expectedGeneration string) (outputEvent, bool) {
	result := outputEvent{Type: "command_result", Operation: "menu_status", State: "failed"}
	if !fixedTimeEqual(expectedGeneration, m.cfg.IdentityGeneration) {
		result.ErrorStage = "identity"
		return result, true
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	inspection, failure := m.inspectCurrent(ctx)
	if failure != nil {
		result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		return result, true
	}
	result.State = "succeeded"
	result.MenuStage = inspection.Stage
	result.MenuDiff = inspection.Diff
	result.VersionState = inspection.VersionState
	result.Version = inspection.Version
	m.lastVersionID = inspection.VersionID
	if inspection.Stage == "submitted_pending_review" || inspection.Stage == "published_pending_visibility" {
		m.submissionRecorded = true
	} else if inspection.Stage == "publish_rejected" {
		m.submissionRecorded = false
	}
	return result, true
}

func (m *menuManager) inspectReadOnly(ctx context.Context, expectedGeneration string) (outputEvent, bool) {
	result, ok := m.status(ctx, expectedGeneration)
	result.Operation = "menu_inspect"
	return result, ok
}

func nextVersion(current string) string {
	parts := strings.Split(strings.TrimSpace(current), ".")
	if len(parts) == 3 {
		major, e1 := strconv.Atoi(parts[0])
		minor, e2 := strconv.Atoi(parts[1])
		patch, e3 := strconv.Atoi(parts[2])
		if e1 == nil && e2 == nil && e3 == nil && major >= 0 && minor >= 0 && patch >= 0 && patch < 999999 {
			return fmt.Sprintf("%d.%d.%d", major, minor, patch+1)
		}
	}
	return "1.0.0"
}
