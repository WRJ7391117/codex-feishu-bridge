package main

import (
	"encoding/json"
	"fmt"
	"regexp"
	"strings"
	"testing"

	sdknormalize "github.com/larksuite/oapi-sdk-go/v3/channel/normalize"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

func resourceEvent(kind, raw string) *larkim.P2MessageReceiveV1 {
	event := makeTextEvent("user")
	event.Event.Message.MessageType, event.Event.Message.Content = &kind, &raw
	return event
}

func TestNormalizeResourceOfficialPayloads(t *testing.T) {
	for _, test := range []struct{ kind, raw, wantKind, key, name string }{
		{"image", `{"image_key":"img_v3_key"}`, "image", "img_v3_key", ""},
		{"file", `{"file_key":"file_key","file_name":"中文资料.pdf"}`, "file", "file_key", "中文资料.pdf"},
		{"audio", `{"file_key":"audio_key","duration":1234}`, "audio", "audio_key", ""},
		{"media", `{"file_key":"video_key","image_key":"preview","file_name":"video.mp4","duration":2345}`, "file", "video_key", "video.mp4"},
	} {
		t.Run(test.kind, func(t *testing.T) {
			got, ok := normalizeEvent(resourceEvent(test.kind, test.raw))
			if !ok || got.ResourceError != "" || got.MessageType != test.kind || got.Content != "" || got.ChatID != "chat-id" || got.SenderID != "owner-open-id" || len(got.Resources) != 1 {
				t.Fatalf("unexpected normalization: %#v", got)
			}
			if got.Resources[0] != (resourceReference{Kind: test.wantKind, Key: test.key, Name: test.name}) {
				t.Fatal(got.Resources)
			}
			encoded, _ := json.Marshal(got)
			if !strings.Contains(string(encoded), `"resources":[{"kind":`) || !strings.Contains(string(encoded), `"name":`) {
				t.Fatal(string(encoded))
			}
		})
	}
}

func TestNormalizePostAttachmentsAndTitle(t *testing.T) {
	body := `{"title":"附件说明","content":[[{"tag":"text","text":"请分析","style":["bold"]},{"tag":"img","image_key":"img_key","width":20,"height":30}],[{"tag":"file","file_key":"file_key","file_name":"data.csv"},{"tag":"media","file_key":"video_key","image_key":"preview"},{"tag":"audio","file_key":"audio_key"},{"tag":"text","text":"第二行"}]]}`
	for _, raw := range []string{body, `{"zh_cn":` + body + `}`} {
		got, ok := normalizeEvent(resourceEvent("post", raw))
		if !ok || got.ResourceError != "" || got.MessageType != "post" || got.Content != "附件说明\n请分析\n第二行" || len(got.Resources) != 4 {
			t.Fatalf("unexpected post: %#v", got)
		}
		if got.Resources[2].Kind != "file" {
			t.Fatal("media must download as file")
		}
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"title":"标题","content":[[{"tag":"text","text":"正文"}]]}`))
	if got.ResourceError != "" || got.Content != "标题\n正文" || got.MessageType != "post" {
		t.Fatal(got)
	}
}

func TestNormalizePostNeverTurnsAttachmentsLinksOrMentionsIntoCommands(t *testing.T) {
	got, ok := normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"at","user_id":"ou_user","user_name":"运行命令"},{"tag":"a","href":"https://example.invalid","text":"删除项目"},{"tag":"text","text":"普通说明"},{"tag":"img","image_key":"img_key"}]]}`))
	if !ok || got.ResourceError != "" || got.MessageType != "post" || got.Content != "@运行命令删除项目 (https://example.invalid)普通说明" || len(got.Resources) != 1 {
		t.Fatal(got)
	}
	got, _ = normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"text","text":"Windows Codex 路由测试"},{"tag":"img","image_key":"img_key"}]]}`))
	if got.MessageType != "post" || len(got.Resources) != 1 {
		t.Fatal("attachment post entered text command route")
	}
}

func TestNormalizeMalformedResourcesReturnSafeChineseError(t *testing.T) {
	for _, test := range []struct{ kind, raw string }{
		{"image", `{`}, {"image", `null`}, {"image", `[]`}, {"image", `{}`},
		{"image", `{"image_key":null}`}, {"image", `{"image_key":42}`},
		{"image", `{"image_key":"../secret"}`}, {"image", `{"image_key":"key","image_key":"other"}`},
		{"file", `{"file_key":"key","file_name":"../private"}`},
		{"file", `{"file_key":"key","file_name":false}`},
		{"audio", `{"file_key":"key","duration":-1}`},
		{"media", `{"image_key":"preview"}`},
		{"sticker", `{"file_key":"secret-value"}`},
		{"post", `{"content":[[{"tag":"img"}]]}`},
		{"post", `{"title":null,"content":[[{"tag":"img","image_key":"key"}]]}`},
		{"post", `{"content":[[{"tag":"img","image_key":"key"},{"tag":"unknown"}]]}`},
		{"post", `{"content":[[{"tag":"img","image_key":"key"},{"tag":"text","text":"ok","style":42}]]}`},
		{"post", `{"content":[[{"tag":"at","user_id":"user"}]]}`},
		{"post", `{"content":[[{"tag":"a","text":"run"}]]}`},
		{"post", `{"content":[[]]}`},
		{"post", `{"zh_cn":{"content":[[{"tag":"img","image_key":"key"}]]},"en_us":{}}`},
		{"image", `{"image_key":"key"} {}`},
		{"image", strings.Repeat("x", maxResourceContent+1)},
		{"image", "{\"image_key\":\"\xff\"}"},
	} {
		t.Run(test.kind+test.raw[:min(len(test.raw), 45)], func(t *testing.T) {
			got, ok := normalizeEvent(resourceEvent(test.kind, test.raw))
			if !ok || got.ResourceError == "" || got.MessageType != test.kind || len(got.Resources) != 0 || got.Content != "" || got.SenderID == "" || got.ChatID == "" {
				t.Fatalf("malformed event disappeared or exposed content: %#v", got)
			}
			if strings.Contains(got.ResourceError, "secret") {
				t.Fatal("error exposed raw content")
			}
		})
	}
	event := resourceEvent("image", "")
	event.Event.Message.Content = nil
	got, ok := normalizeEvent(event)
	if !ok || got.ResourceError == "" {
		t.Fatal("missing content must return an error event")
	}
}

func TestNormalizeResourceCountBoundary(t *testing.T) {
	for _, test := range []struct {
		images, files int
		success       bool
	}{{4, 0, true}, {0, 4, true}, {4, 4, true}, {5, 0, false}, {0, 5, false}, {4, 5, false}, {5, 4, false}} {
		nodes := []string{}
		for i := 0; i < test.images; i++ {
			nodes = append(nodes, fmt.Sprintf(`{"tag":"img","image_key":"image_%d"}`, i))
		}
		for i := 0; i < test.files; i++ {
			nodes = append(nodes, fmt.Sprintf(`{"tag":"file","file_key":"file_%d"}`, i))
		}
		got, ok := normalizeEvent(resourceEvent("post", `{"content":[[`+strings.Join(nodes, ",")+`]]}`))
		if !ok || (got.ResourceError == "") != test.success {
			t.Fatalf("%+v: %#v", test, got)
		}
		if test.success && len(got.Resources) != test.images+test.files {
			t.Fatal(got)
		}
		if !test.success && len(got.Resources) != 0 {
			t.Fatal(got)
		}
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"content":[[`+strings.Repeat(`{"tag":"img","image_key":"same_key"},`, 12)+`{"tag":"img","image_key":"same_key"}]]}`))
	if got.ResourceError != "" || len(got.Resources) != 1 {
		t.Fatal("dedup must precede resource caps", got)
	}
	got, _ = normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"file","file_key":"same_key"},{"tag":"file","file_key":"same_key","file_name":"name.txt"}]]}`))
	if got.ResourceError != "" || len(got.Resources) != 1 || got.Resources[0].Name != "name.txt" {
		t.Fatal(got)
	}
	got, _ = normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"img","image_key":"same_key"},{"tag":"file","file_key":"same_key"}]]}`))
	if got.ResourceError == "" || len(got.Resources) != 0 {
		t.Fatal("conflicting resource identity must fail", got)
	}
}

func TestNormalizeOfficialImageEventJSONFixture(t *testing.T) {
	// Official im.message.receive_v1 envelope shape, with synthetic identifiers.
	const fixture = `{"schema":"2.0","header":{"event_id":"event_fixture","event_type":"im.message.receive_v1","create_time":"1661234567890","token":"fixture","app_id":"cli_fixture","tenant_key":"tenant_fixture"},"event":{"sender":{"sender_id":{"union_id":"on_fixture","user_id":"user_fixture","open_id":"ou_fixture"},"sender_type":"user","tenant_key":"tenant_fixture"},"message":{"message_id":"om_fixture","root_id":"","parent_id":"","create_time":"1661234567890","chat_id":"oc_fixture","chat_type":"p2p","message_type":"image","content":"{\"image_key\":\"img_v3_02ab_12345678-1234-1234-1234-123456789abc\"}"}}}`
	var event larkim.P2MessageReceiveV1
	if err := json.Unmarshal([]byte(fixture), &event); err != nil {
		t.Fatal(err)
	}
	got, ok := normalizeEvent(&event)
	if !ok || got.ResourceError != "" || got.ChatID != "oc_fixture" || got.SenderID != "ou_fixture" || got.MessageID != "om_fixture" || len(got.Resources) != 1 || got.Resources[0].Key != "img_v3_02ab_12345678-1234-1234-1234-123456789abc" {
		t.Fatal(got)
	}
}

func TestNormalizeResourcesRequireOfficialIdentity(t *testing.T) {
	for _, mutate := range []func(*larkim.P2MessageReceiveV1){
		func(e *larkim.P2MessageReceiveV1) { e.Event.Sender = nil },
		func(e *larkim.P2MessageReceiveV1) { e.Event.Sender.SenderId = nil },
		func(e *larkim.P2MessageReceiveV1) { e.Event.Sender.SenderId.OpenId = nil },
		func(e *larkim.P2MessageReceiveV1) { e.Event.Message.ChatId = nil },
		func(e *larkim.P2MessageReceiveV1) { e.Event.Message.MessageId = nil },
	} {
		event := resourceEvent("image", `{"image_key":"key"}`)
		mutate(event)
		if _, ok := normalizeEvent(event); ok {
			t.Fatal("identity missing")
		}
	}
}

func TestNormalizePostBuiltByInstalledOfficialSDK(t *testing.T) {
	// Primary source: oapi-sdk-go/v3@v3.11.0/service/im/v1/ext_model.go,
	// MessagePostText.UnEscape and MessagePostImage. This is generated by that
	// SDK, not a purported capture of the user's failed production message.
	content := larkim.NewMessagePostContent().ContentTitle("截图说明").
		AppendContent([]larkim.MessagePostElement{&larkim.MessagePostText{Text: "请检查 &lt;button&gt; 与 &amp;", UnEscape: true}}).
		AppendContent([]larkim.MessagePostElement{&larkim.MessagePostImage{ImageKey: "img_sdk_screenshot"}}).
		Build()
	raw, err := larkim.NewMessagePost().ZhCn(content).Build()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(raw, `"un_escape":true`) {
		t.Fatal("SDK fixture did not exercise un_escape")
	}
	got, ok := normalizeEvent(resourceEvent("post", raw))
	if !ok || got.ResourceError != "" || got.Content != "截图说明\n请检查 <button> 与 &" || got.MessageType != "post" || len(got.Resources) != 1 || got.Resources[0].Key != "img_sdk_screenshot" {
		t.Fatalf("official SDK post failed: %#v", got)
	}
	if got.ResourceErrorCode != "" || got.ResourceErrorPath != "" {
		t.Fatal("successful event has diagnostic")
	}
}

func TestNormalizeScreenshotTextCompatibilityCandidates(t *testing.T) {
	// These reproduce candidate causes, not the still-unavailable incident payload.
	// Optional width/height (including null) and lines are emitted by the official
	// larksuite/feishu MessageImage/MessageText models in feishu/dt_message.py.
	// Fractional numeric dimensions are a presentation-only compatibility choice.
	for _, test := range []struct {
		name, raw, want string
		count           int
	}{
		{"unescape_false", `{"content":[[{"tag":"text","text":"&lt;原文&gt;","un_escape":false},{"tag":"img","image_key":"img_one"}]]}`, "&lt;原文&gt;", 1},
		{"unescape_true", `{"content":[[{"tag":"text","text":"&#x4e2d; &amp;lt;","un_escape":true},{"tag":"img","image_key":"img_one"}]]}`, "中 &lt;", 1},
		{"dimensions", `{"content":[[{"tag":"text","text":"截图","style":["bold","italic","underline","lineThrough"],"lines":1},{"tag":"img","image_key":"img_one","width":1365.5,"height":768.0}]]}`, "截图", 1},
		{"null_dimensions", `{"content":[[{"tag":"text","text":"截图"},{"tag":"img","image_key":"img_one","width":null,"height":null}]]}`, "截图", 1},
		{"blank_lines", `{"title":"标题","content":[[],[{"tag":"text","text":"先看第一张"},{"tag":"img","image_key":"img_one"}],[],[{"tag":"text","text":""}],[{"tag":"text","text":"再看第二张"},{"tag":"img","image_key":"img_two"}],[]]}`, "标题\n\n先看第一张\n\n\n再看第二张\n", 2},
		{"image_only_with_blanks", `{"content":[[],[{"tag":"img","image_key":"img_one"}],[]]}`, "\n", 1},
		{"sdk_empty_v2_fallback", `{"content_v2":[],"content":[[{"tag":"text","text":"截图"},{"tag":"img","image_key":"img_one"}]]}`, "截图", 1},
		{"text_not_clipped", `{"content":[[{"tag":"text","text":"第一行\n第二行","lines":1},{"tag":"img","image_key":"img_one"}]]}`, "第一行\n第二行", 1},
	} {
		t.Run(test.name, func(t *testing.T) {
			for _, raw := range []string{test.raw, `{"zh_cn":` + test.raw + `}`} {
				got, ok := normalizeEvent(resourceEvent("post", raw))
				if !ok || got.ResourceError != "" || got.ResourceErrorCode != "" || got.ResourceErrorPath != "" || got.MessageType != "post" || got.Content != test.want || len(got.Resources) != test.count {
					t.Fatalf("candidate shape rejected or lost content: %#v", got)
				}
			}
		})
	}
}

func TestNormalizePostMetadataRejectsInvalidTypesAndUnknownFields(t *testing.T) {
	for _, test := range []struct{ node, code, suffix string }{
		{`{"tag":"text","text":"secret","un_escape":"true"}`, "invalid_field", ".un_escape"},
		{`{"tag":"text","text":"secret","un_escape":null}`, "invalid_field", ".un_escape"},
		{`{"tag":"text","text":"secret","un_escape":1}`, "invalid_field", ".un_escape"},
		{`{"tag":"text","text":"secret","style":{}}`, "invalid_field", ".style"},
		{`{"tag":"text","text":"secret","style":["credential-value"]}`, "invalid_field", ".style"},
		{`{"tag":"text","text":"secret","lines":1.5}`, "invalid_field", ".lines"},
		{`{"tag":"text","text":"secret","lines":null}`, "invalid_field", ".lines"},
		{`{"tag":"text","text":"secret","lines":-1}`, "invalid_field", ".lines"},
		{`{"tag":"img","image_key":"img_private","width":"1024"}`, "invalid_field", ".width"},
		{`{"tag":"img","image_key":"img_private","height":-1}`, "invalid_field", ".height"},
		{`{"tag":"img","image_key":"img_private","width":1e999}`, "invalid_field", ".width"},
		{`{"tag":"img","image_key":"img_private","height":false}`, "invalid_field", ".height"},
		{`{"tag":"img","image_key":"img_private","image_type":"unknown-value"}`, "unsupported_field", ".image_type"},
		{`{"tag":"img","image_key":"img_private","style":{"secret":"secret"}}`, "unsupported_field", ".style"},
		{`{"tag":"img","image_key":"img_private","cli_secret_field":true}`, "unsupported_field", ".unknown"},
		{`{"tag":"token-secret-node","text":"secret"}`, "unsupported_tag", ".tag"},
		{`{"tag":123}`, "invalid_field", ".tag"},
	} {
		got, ok := normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"img","image_key":"img_valid"},`+test.node+`]]}`))
		if !ok || got.ResourceError == "" || got.ResourceErrorCode != test.code || got.ResourceErrorPath != "post.content[0][1]"+test.suffix || got.Content != "" || len(got.Resources) != 0 {
			t.Fatalf("unsafe node not rejected atomically: %#v", got)
		}
		encoded, _ := json.Marshal(got)
		for _, secret := range []string{"img_private", "img_valid", "cli_secret_field", "token-secret-node", "credential-value", "unknown-value"} {
			if strings.Contains(string(encoded), secret) {
				t.Fatal("raw field/tag/value leaked in error")
			}
		}
	}
}

func TestNormalizePostDiagnosticBranches(t *testing.T) {
	for _, test := range []struct{ raw, code, path string }{
		{`null`, "invalid_content", "post"},
		{`{"zh_cn":null}`, "invalid_content", "post.locale"},
		{`{"title":123,"content":[]}`, "invalid_field", "post.title"},
		{`{"content":[null]}`, "invalid_content", "post.content[0]"},
		{`{"content":[[null]]}`, "invalid_content", "post.content[0][0]"},
		{`{"content":[[{"tag":"img"}]]}`, "invalid_field", "post.content[0][0].image_key"},
		{`{"content":[[]]}`, "empty_content", "post.content"},
		{`{"content":[[{"tag":"a","text":"Run","href":"https://example.invalid"}]]}`, "interactive_only", "post.content"},
		{`{"secret_field":"secret","content":[]}`, "unsupported_field", "post.unknown"},
		{`{"tag":"secret","content":[]}`, "unsupported_field", "post.unknown"},
		{`{"content_v2":[[{"tag":"unknown","text":"other"}]],"content":[[{"tag":"text","text":"kept?"}]]}`, "unsupported_tag", "post.content_v2[0][0].tag"},
		{`{"content":[[{"tag":"img","image_key":"duplicate"},{"tag":"file","file_key":"duplicate"}]]}`, "resource_conflict", "post.content[0][1]"},
	} {
		got, ok := normalizeEvent(resourceEvent("post", test.raw))
		if !ok || got.ResourceErrorCode != test.code || got.ResourceErrorPath != test.path || got.Content != "" || len(got.Resources) != 0 {
			t.Fatalf("unexpected diagnostic: %#v", got)
		}
	}
	nodes := []string{}
	for i := 0; i < 5; i++ {
		nodes = append(nodes, fmt.Sprintf(`{"tag":"img","image_key":"img_%d","width":12.5}`, i))
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"content":[[`+strings.Join(nodes, ",")+`]]}`))
	if got.ResourceErrorCode != "resource_limit" || got.ResourceErrorPath != "post.content[0][4]" || len(got.Resources) != 0 {
		t.Fatal(got)
	}
}

func TestNormalizeResourceErrorProtocolFiniteAndSchemaOnly(t *testing.T) {
	known := map[string]bool{"missing_content": true, "content_too_large": true, "invalid_json": true, "invalid_content": true, "invalid_field": true, "unsupported_field": true, "unsupported_tag": true, "unsupported_message_type": true, "resource_conflict": true, "resource_limit": true, "empty_content": true, "interactive_only": true}
	grammar := regexp.MustCompile(`^(message\.(message_type|content(\.(image_key|file_key|file_name|width|height|duration|tag|image_type|style|unknown))?)|post(\.(locale|title|unknown|content(_v2)?(\[[0-9]+\](\[[0-9]+\](\.(tag|text|style|un_escape|lines|image_key|file_key|file_name|width|height|duration|image_type|href|user_id|user_name|unknown))?)?)?))?)$`)
	for _, test := range []struct{ kind, raw string }{
		{"post", `{"content":[[{"tag":"sensitive-tag","sensitive-field":"token"}]]}`},
		{"post", `{"content":[[{"tag":"text","text":"token","sensitive-field":"token"}]]}`},
		{"post", `{"secret":{},"content":[]}`},
		{"image", `{"image_key":"private/key"}`},
		{"file", `{"file_key":"private","secret":"token"}`},
		{"post", `{`}, {"post", strings.Repeat("x", maxResourceContent+1)},
		{"sensitive-message-type", `{}`},
	} {
		got, ok := normalizeEvent(resourceEvent(test.kind, test.raw))
		if !ok || !known[got.ResourceErrorCode] || !grammar.MatchString(got.ResourceErrorPath) {
			t.Fatal("unbounded diagnostic", got)
		}
		encoded, _ := json.Marshal(struct {
			Code string `json:"resource_error_code"`
			Path string `json:"resource_error_path"`
		}{got.ResourceErrorCode, got.ResourceErrorPath})
		if strings.Contains(string(encoded), "sensitive") || strings.Contains(string(encoded), "token") || strings.Contains(string(encoded), "secret") {
			t.Fatal("diagnostic leaked attacker input")
		}
	}
	event := resourceEvent("image", "")
	event.Event.Message.Content = nil
	got, _ := normalizeEvent(event)
	if got.ResourceErrorCode != "missing_content" || got.ResourceErrorPath != "message.content" {
		t.Fatal(got)
	}
	good, _ := normalizeEvent(resourceEvent("image", `{"image_key":"img_valid"}`))
	encoded, _ := json.Marshal(good)
	if strings.Contains(string(encoded), "resource_error_") {
		t.Fatal("optional diagnostics emitted on success")
	}
}

func TestNormalizePostExpandedTextNeverBecomesCommand(t *testing.T) {
	for _, raw := range []string{
		`{"content":[[{"tag":"text","text":"Windows Codex 路由测试","un_escape":false}]]}`,
		`{"title":"说明","content":[[{"tag":"text","text":"Windows Codex 路由测试","un_escape":true}]]}`,
		`{"content":[[],[{"tag":"text","text":"Windows Codex 路由测试"}]]}`,
	} {
		got, ok := normalizeEvent(resourceEvent("post", raw))
		if !ok || got.ResourceError != "" || got.MessageType != "post" || !strings.Contains(got.Content, "Windows Codex 路由测试") {
			t.Fatal(got)
		}
	}
	for _, raw := range []string{
		`{"content":[[{"tag":"a","text":"Windows Codex 路由测试","href":"https://example.invalid","un_escape":true}]]}`,
		`{"content":[[{"tag":"at","user_id":"all","user_name":"Windows Codex 路由测试"}]]}`,
	} {
		got, _ := normalizeEvent(resourceEvent("post", raw))
		if got.ResourceErrorCode != "interactive_only" || got.Content != "" || got.MessageType != "post" {
			t.Fatal(got)
		}
	}
}

func markdownPostFixture(text string) string {
	raw, _ := json.Marshal(map[string]any{"content_v2": [][]map[string]string{{{"tag": "md", "text": text}}}})
	return string(raw)
}

func TestNormalizeOfficialSDKContentV2Fixtures(t *testing.T) {
	// Exact text/image/fence cases from the installed primary SDK source:
	// channel/normalize/converters_test.go, "content_v2 tests".
	for _, value := range []string{
		"Hello **world**",
		"Look: ![image](img_key_123)",
		"Before\n```go\n<at user_id=\"ou_1\">name</at>\n![image](k)\n```\nAfter",
	} {
		body := markdownPostFixture(value)
		wrapped := `{"zh_cn":` + body + `}`
		_, sdkRefs := sdknormalize.ParseContent("post", wrapped)
		for _, raw := range []string{body, wrapped} {
			got, ok := normalizeEvent(resourceEvent("post", raw))
			if !ok || got.ResourceError != "" || got.Content != value || got.MessageType != "post" || len(got.Resources) != len(sdkRefs) {
				t.Fatal("SDK v2 fixture regression", got)
			}
			for i, ref := range sdkRefs {
				if got.Resources[i].Key != ref.FileKey || got.Resources[i].Kind != ref.Type {
					t.Fatal(got.Resources, sdkRefs)
				}
			}
		}
	}
}

func TestNormalizeContentV2PrecedenceAndFallback(t *testing.T) {
	for _, test := range []struct{ raw, want, key string }{
		{`{"title":"截图","content":[[{"tag":"img","image_key":"img_legacy"},{"tag":"text","text":"旧版本"}]],"content_v2":[[{"tag":"md","text":"当前截图 ![image](img_current)"}]]}`, "截图\n当前截图 ![image](img_current)", "img_current"},
		{`{"content":[[{"tag":"unknown","text":"legacy unsupported"}]],"content_v2":[[{"tag":"md","text":"![image](img_current)"}]]}`, "![image](img_current)", "img_current"},
		{`{"content":[[{"tag":"text","text":"回退"},{"tag":"img","image_key":"img_legacy"}]],"content_v2":[]}`, "回退", "img_legacy"},
	} {
		got, ok := normalizeEvent(resourceEvent("post", test.raw))
		if !ok || got.ResourceError != "" || got.Content != test.want || got.MessageType != "post" || len(got.Resources) != 1 || got.Resources[0].Key != test.key {
			t.Fatal(got)
		}
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"img","image_key":"img_legacy"}]],"content_v2":[[{"tag":"md","text":"只有新版文字"}]]}`))
	if got.ResourceError != "" || got.Content != "只有新版文字" || len(got.Resources) != 0 || got.MessageType != "post" {
		t.Fatal("legacy assets must not be merged", got)
	}
	got, _ = normalizeEvent(resourceEvent("post", `{"content":[[{"tag":"img","image_key":"img_legacy"}]],"content_v2":[[{"tag":"secret_unknown"}]]}`))
	if got.ResourceErrorCode != "unsupported_tag" || got.ResourceErrorPath != "post.content_v2[0][0].tag" || len(got.Resources) != 0 {
		t.Fatal("invalid authoritative version fell back silently", got)
	}
}

func TestNormalizeMarkdownCodeAndEscapesNeverDownloadExamples(t *testing.T) {
	for _, value := range []string{
		"```go\n![image](secret_code_key)\n```\n![image](img_real)",
		"~~~markdown\n![image](secret_code_key)\n~~~\n![image](img_real)",
		"````markdown\n```\n![image](secret_code_key)\n```\n````\n![image](img_real)",
		"`![image](secret_code_key)` and ![image](img_real)",
		"``before ` ![image](secret_code_key) after`` and ![image](img_real)",
		"`before\n![image](secret_code_key)\nafter` and ![image](img_real)",
		"    ![image](secret_code_key)\n![image](img_real)",
		"\t![image](secret_code_key)\n![image](img_real)",
		"\\![image](secret_code_key) and ![image](img_real)",
		"![image](img_real)\n```\n![image](secret_code_key)",
		"![image](img_real)\n~~~\n![image](secret_code_key)",
		"   ```\n![image](secret_code_key)\n   ```\n![image](img_real)",
	} {
		got, ok := normalizeEvent(resourceEvent("post", markdownPostFixture(value)))
		if !ok || got.ResourceError != "" || got.Content != value || len(got.Resources) != 1 || got.Resources[0].Key != "img_real" {
			t.Fatalf("code example extracted or original text changed: %#v", got)
		}
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"content_v2":[[{"tag":"md","text":"`+"```md"+`"}],[{"tag":"md","text":"![image](secret_code_key)"}],[{"tag":"md","text":"`+"```"+`\n![image](img_real)"}]]}`))
	if got.ResourceError != "" || len(got.Resources) != 1 || got.Resources[0].Key != "img_real" {
		t.Fatal("fence across nodes lost", got)
	}
}

func TestNormalizeMarkdownUnmatchedInlineBacktickPreservesRealImages(t *testing.T) {
	for _, value := range []string{
		"请看 ` 报错\n![image](img_real)",
		"请看 `` 报错\n![image](img_real)",
		"请看 ` 报错 ``\n![image](img_real)",
		"先看 `示例`，再看 ` 报错\n![image](img_real)",
	} {
		got, ok := normalizeEvent(resourceEvent("post", markdownPostFixture(value)))
		if !ok || got.ResourceError != "" || got.ResourceErrorCode != "" || got.Content != value || got.MessageType != "post" || len(got.Resources) != 1 || got.Resources[0].Key != "img_real" {
			t.Fatalf("unmatched inline backtick lost real image: %#v", got)
		}
	}
	// Inline matching is bounded to one md node; an unmatched delimiter must
	// never leave masking state that consumes resources from the next node.
	got, _ := normalizeEvent(resourceEvent("post", `{"content_v2":[[{"tag":"md","text":"请看 `+"`"+` 报错"}],[{"tag":"md","text":"![image](img_real)"}]]}`))
	if got.ResourceError != "" || got.Content != "请看 ` 报错\n![image](img_real)" || len(got.Resources) != 1 || got.Resources[0].Key != "img_real" {
		t.Fatal("unmatched delimiter leaked across md nodes", got)
	}
}

func TestNormalizeMarkdownKeySafetyDedupAndLimits(t *testing.T) {
	for _, key := range []string{"https://example.invalid/private.png", "../private", "img key", "img_private?token=secret", "C:\\private"} {
		got, _ := normalizeEvent(resourceEvent("post", markdownPostFixture("![image]("+key+")")))
		if got.ResourceErrorCode != "invalid_field" || got.ResourceErrorPath != "post.content_v2[0][0].text" || got.Content != "" || len(got.Resources) != 0 {
			t.Fatal("untrusted markdown image destination accepted", got)
		}
	}
	for _, count := range []int{4, 5} {
		var text strings.Builder
		for i := 0; i < count; i++ {
			fmt.Fprintf(&text, "![image](img_%d) ", i)
		}
		got, _ := normalizeEvent(resourceEvent("post", markdownPostFixture(text.String())))
		if count == 4 && (got.ResourceError != "" || len(got.Resources) != 4) {
			t.Fatal(got)
		}
		if count == 5 && (got.ResourceErrorCode != "resource_limit" || got.ResourceErrorPath != "post.content_v2[0][0]" || len(got.Resources) != 0) {
			t.Fatal(got)
		}
	}
	got, _ := normalizeEvent(resourceEvent("post", `{"content_v2":[[{"tag":"md","text":"![image](img_same) ![image](img_same)"},{"tag":"img","image_key":"img_same"}]]}`))
	if got.ResourceError != "" || len(got.Resources) != 1 {
		t.Fatal("markdown and structured resources not deduplicated", got)
	}
	got, _ = normalizeEvent(resourceEvent("post", `{"content_v2":[[{"tag":"md","text":"![image](same_key)"},{"tag":"file","file_key":"same_key"}]]}`))
	if got.ResourceErrorCode != "resource_conflict" || len(got.Resources) != 0 {
		t.Fatal(got)
	}
}

func TestNormalizeMarkdownDoesNotCreateCommandsOrLoseMentions(t *testing.T) {
	value := `<at user_id="ou_user">用户</at> Windows Codex 路由测试 ![image](img_real)`
	got, _ := normalizeEvent(resourceEvent("post", markdownPostFixture(value)))
	if got.ResourceError != "" || got.MessageType != "post" || got.Content != value || len(got.Resources) != 1 {
		t.Fatal(got)
	}
	got, _ = normalizeEvent(resourceEvent("post", markdownPostFixture("Windows Codex 路由测试")))
	if got.ResourceError != "" || got.MessageType != "post" || got.Content != "Windows Codex 路由测试" {
		t.Fatal("markdown became command", got)
	}
	for _, text := range []string{`[运行](https://example.invalid)`, `<at user_id="ou_user">运行</at>`} {
		got, _ := normalizeEvent(resourceEvent("post", markdownPostFixture(text)))
		if got.ResourceErrorCode != "interactive_only" || got.Content != "" || got.MessageType != "post" {
			t.Fatal(got)
		}
	}
}
