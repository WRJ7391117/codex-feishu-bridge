package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"html"
	"io"
	"math"
	"regexp"
	"sort"
	"strings"
	"unicode"
	"unicode/utf8"

	sdknormalize "github.com/larksuite/oapi-sdk-go/v3/channel/normalize"
	channeltypes "github.com/larksuite/oapi-sdk-go/v3/channel/types"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

const maxResourceCount = 8
const maxImageResources = 4
const maxFileResources = 4
const maxResourceContent = 64 * 1024

type resourceReference struct {
	Kind string `json:"kind"`
	Key  string `json:"key"`
	Name string `json:"name"`
}

func resourceString(p *string) string {
	if p == nil {
		return ""
	}
	return *p
}

func validResourceID(s string) bool {
	if s == "" || len(s) > 512 {
		return false
	}
	for _, r := range s {
		if !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '_' || r == '-') {
			return false
		}
	}
	return true
}

func validResourceName(s string) bool {
	return len(s) <= 255 && utf8.ValidString(s) && s != "." && s != ".." &&
		!strings.ContainsAny(s, `/\:`) && strings.IndexFunc(s, unicode.IsControl) < 0
}

// Parse the official raw content, never the SDK's synthesized markdown. In
// particular, link labels and mentions cannot become executable command text.
func normalizeResourceEvent(event *larkim.P2MessageReceiveV1) (outputEvent, bool) {
	data := event.Event
	message := data.Message
	if data.Sender == nil || data.Sender.SenderId == nil {
		return outputEvent{}, false
	}
	result := outputEvent{
		Type: "im.message.receive_v1", ChatID: resourceString(message.ChatId),
		ChatType: resourceString(message.ChatType), MessageID: resourceString(message.MessageId),
		SenderID: resourceString(data.Sender.SenderId.OpenId), SenderType: resourceString(data.Sender.SenderType),
		MessageType: resourceString(message.MessageType), ContentState: "resource-json-decoded",
	}
	if !validResourceID(result.SenderID) || !validResourceID(result.ChatID) || !validResourceID(result.MessageID) {
		return outputEvent{}, false
	}
	fail := func(reason, code, path string) (outputEvent, bool) {
		result.Content, result.Resources, result.ResourceError = "", nil, reason
		result.ResourceErrorCode, result.ResourceErrorPath = code, path
		return result, true
	}
	if message.Content == nil {
		return fail("附件消息内容缺失，请重新发送。", "missing_content", "message.content")
	}
	if len(*message.Content) > maxResourceContent {
		return fail("附件消息内容过大，请重新发送。", "content_too_large", "message.content")
	}
	if !unambiguousJSON([]byte(*message.Content)) {
		return fail("附件消息格式无效，请重新发送。", "invalid_json", "message.content")
	}
	raw := []byte(*message.Content)
	switch result.MessageType {
	case "image", "file", "audio", "media":
		var node map[string]json.RawMessage
		if json.Unmarshal(raw, &node) != nil {
			return fail("附件消息格式无效，请重新发送。", "invalid_content", "message.content")
		}
		ref, diagnostic := decodeResourceNodeDetailed(node, result.MessageType, false, "message.content")
		if diagnostic != nil {
			return fail("附件信息缺失或格式无效，请重新发送。", diagnostic.code, diagnostic.path)
		}
		result.Resources = []resourceReference{ref}
	case "post":
		if decoded, ok := decodePlainTextPostEnvelope(raw); ok {
			result.Content, result.MessageType, result.ContentState = decoded, "text", "post-text-decoded"
			// Preserve the existing official mention-key behavior for plain posts.
			for _, mention := range message.Mentions {
				if mention != nil && mention.Key != nil {
					result.Content = stripOfficialMentionKeys(result.Content, []channeltypes.Mention{{Key: *mention.Key}})
				}
			}
			return result, true
		}
		text, refs, diagnostic := decodeResourcePostDetailed(raw)
		if diagnostic != nil {
			return fail(diagnostic.message(), diagnostic.code, diagnostic.path)
		}
		result.Content, result.Resources = text, refs
	default:
		return fail("暂不支持此消息附件类型，请发送图片、文件或音频。", "unsupported_message_type", "message.message_type")
	}
	return result, true
}

func decodeResourceNode(node map[string]json.RawMessage, kind string, post bool) (resourceReference, bool) {
	ref, diagnostic := decodeResourceNodeDetailed(node, kind, post, "message.content")
	return ref, diagnostic == nil
}

type resourceNormalizationError struct{ code, path string }

func (e *resourceNormalizationError) message() string {
	if e.code == "resource_limit" {
		return "附件数量过多，每条消息最多支持 4 张图片和 4 个文件或音频。"
	}
	return "富文本附件格式无效或包含不支持的内容，请重新发送。"
}

// Only these schema field names may enter diagnostics. Never copy an unknown
// object key or any tag value from the incoming event into the protocol.
func resourceFieldPath(path, field string) string {
	switch field {
	case "title", "content", "content_v2", "tag", "text", "style", "un_escape", "lines", "image_key", "file_key", "file_name", "width", "height", "duration", "image_type", "href", "user_id", "user_name":
		return path + "." + field
	default:
		return path + ".unknown"
	}
}

func resourceFields(node map[string]json.RawMessage) []string {
	fields := make([]string, 0, len(node))
	for field := range node {
		fields = append(fields, field)
	}
	sort.Strings(fields)
	return fields
}

func decodeResourceNodeDetailed(node map[string]json.RawMessage, kind string, post bool, path string) (resourceReference, *resourceNormalizationError) {
	ref := resourceReference{Kind: kind}
	invalid := func(code, field string) (resourceReference, *resourceNormalizationError) {
		return resourceReference{}, &resourceNormalizationError{code, resourceFieldPath(path, field)}
	}
	keyField := "file_key"
	if kind == "image" || kind == "img" {
		ref.Kind, keyField = "image", "image_key"
	}
	if kind == "media" {
		ref.Kind = "file"
	}
	if ref.Kind != "image" && ref.Kind != "file" && ref.Kind != "audio" {
		return invalid("unsupported_tag", "tag")
	}
	if !jsonString(node[keyField], &ref.Key) || !validResourceID(ref.Key) {
		return invalid("invalid_field", keyField)
	}
	if name, exists := node["file_name"]; exists {
		if !jsonString(name, &ref.Name) || !validResourceName(ref.Name) {
			return invalid("invalid_field", "file_name")
		}
	}
	for _, key := range resourceFields(node) {
		value := node[key]
		switch key {
		case keyField, "file_name":
		case "tag":
			if !post {
				return invalid("unsupported_field", key)
			}
		case "image_key": // media preview is metadata, not a second attachment
			var preview string
			if kind != "media" || !jsonString(value, &preview) || !validResourceID(preview) {
				return invalid("invalid_field", key)
			}
		case "width", "height":
			// larksuite/feishu MessageImage.as_post_dict emits optional dimensions,
			// including null. JSON numeric display dimensions need not be integers.
			if !validResourceDimension(value) {
				return invalid("invalid_field", key)
			}
		case "duration":
			var n int64
			if bytes.Equal(value, []byte("null")) || json.Unmarshal(value, &n) != nil || n < 0 {
				return invalid("invalid_field", key)
			}
		default:
			return invalid("unsupported_field", key)
		}
	}
	return ref, nil
}

func validResourceDimension(value json.RawMessage) bool {
	if bytes.Equal(value, []byte("null")) {
		return true
	}
	var n float64
	return json.Unmarshal(value, &n) == nil && !math.IsInf(n, 0) && !math.IsNaN(n) && n >= 0 && n <= math.MaxInt32
}

func decodeResourcePost(raw []byte) (string, []resourceReference, string) {
	text, refs, diagnostic := decodeResourcePostDetailed(raw)
	if diagnostic != nil {
		return "", nil, diagnostic.message()
	}
	return text, refs, ""
}

func decodeResourcePostDetailed(raw []byte) (string, []resourceReference, *resourceNormalizationError) {
	bad := func(code, path string) (string, []resourceReference, *resourceNormalizationError) {
		return "", nil, &resourceNormalizationError{code, path}
	}
	if len(raw) > maxResourceContent {
		return bad("content_too_large", "post")
	}
	if !unambiguousJSON(raw) {
		return bad("invalid_json", "post")
	}
	var body map[string]json.RawMessage
	if json.Unmarshal(raw, &body) != nil || len(body) == 0 {
		return bad("invalid_content", "post")
	}
	if len(body) == 1 {
		for key, value := range body {
			if isFeishuLocaleKey(key) {
				// Decode into a fresh map: Unmarshal does not clear an existing map.
				var localized map[string]json.RawMessage
				if json.Unmarshal(value, &localized) != nil || len(localized) == 0 {
					return bad("invalid_content", "post.locale")
				}
				body = localized
				break
			}
		}
	}
	contentField := "content"
	for _, key := range resourceFields(body) {
		if key == "content_v2" {
			// Official SDK converters.go: authoritative nonempty v2, otherwise v1.
			var alternative []json.RawMessage
			if json.Unmarshal(body[key], &alternative) != nil || alternative == nil {
				return bad("invalid_field", "post.content_v2")
			}
			if len(alternative) != 0 {
				contentField = "content_v2"
			}
		} else if key != "title" && key != "content" {
			return bad("unsupported_field", "post.unknown")
		}
	}
	var title string
	if value, ok := body["title"]; ok && !jsonString(value, &title) {
		return bad("invalid_field", "post.title")
	}
	var paragraphs []json.RawMessage
	contentPath := "post." + contentField
	if json.Unmarshal(body[contentField], &paragraphs) != nil || len(paragraphs) == 0 {
		return bad("invalid_content", contentPath)
	}
	lines := []string{}
	if title != "" {
		lines = append(lines, title)
	}
	refs := []resourceReference{}
	seen := make(map[string]int)
	images, files := 0, 0
	addResource := func(ref resourceReference, path string) *resourceNormalizationError {
		if index, exists := seen[ref.Key]; exists {
			previous := &refs[index]
			if previous.Kind != ref.Kind || previous.Name != "" && ref.Name != "" && previous.Name != ref.Name {
				return &resourceNormalizationError{"resource_conflict", path}
			}
			if previous.Name == "" {
				previous.Name = ref.Name
			}
			return nil
		}
		seen[ref.Key] = len(refs)
		refs = append(refs, ref)
		if ref.Kind == "image" {
			images++
		} else {
			files++
		}
		if images > maxImageResources || files > maxFileResources || len(refs) > maxResourceCount {
			return &resourceNormalizationError{"resource_limit", path}
		}
		return nil
	}
	var markdownState markdownResourceState
	interactive := false
	for paragraphIndex, rawParagraph := range paragraphs {
		paragraphPath := fmt.Sprintf("%s[%d]", contentPath, paragraphIndex)
		var paragraph []json.RawMessage
		if json.Unmarshal(rawParagraph, &paragraph) != nil || paragraph == nil {
			return bad("invalid_content", paragraphPath)
		}
		// Empty arrays and explicit empty text nodes represent visual blank lines.
		var text strings.Builder
		hasText := len(paragraph) == 0
		for nodeIndex, rawNode := range paragraph {
			path := fmt.Sprintf("%s[%d]", paragraphPath, nodeIndex)
			var node map[string]json.RawMessage
			if json.Unmarshal(rawNode, &node) != nil || len(node) == 0 {
				return bad("invalid_content", path)
			}
			var tag string
			if !jsonString(node["tag"], &tag) {
				return bad("invalid_field", path+".tag")
			}
			switch tag {
			case "md":
				// The installed SDK's content_v2 fixtures define md text and image
				// extraction. Keep raw markdown intact and use its tested extractor
				// only on text outside code; never promote md to a text command.
				var value string
				if !jsonString(node["text"], &value) {
					return bad("invalid_field", path+".text")
				}
				for _, key := range resourceFields(node) {
					if key != "tag" && key != "text" {
						return bad("unsupported_field", resourceFieldPath(path, key))
					}
				}
				visible := markdownState.outsideCode(value)
				if postMarkdownInteractivePattern.MatchString(visible) {
					interactive = true
				}
				fixture, _ := json.Marshal(map[string]any{"zh_cn": map[string]any{"content_v2": [][]map[string]string{{{"tag": "md", "text": visible}}}}})
				_, extracted := sdknormalize.ParseContent("post", string(fixture))
				for _, resource := range extracted {
					if resource.Type != "image" || !validResourceID(resource.FileKey) {
						return bad("invalid_field", path+".text")
					}
					if diagnostic := addResource(resourceReference{Kind: "image", Key: resource.FileKey}, path); diagnostic != nil {
						return "", nil, diagnostic
					}
				}
				text.WriteString(value)
				hasText = true
			case "text":
				value, diagnostic := decodePostText(node, path)
				if diagnostic != nil {
					return "", nil, diagnostic
				}
				text.WriteString(value)
				hasText = true
			case "img", "file", "media", "audio":
				ref, diagnostic := decodeResourceNodeDetailed(node, tag, true, path)
				if diagnostic != nil {
					return "", nil, diagnostic
				}
				if diagnostic := addResource(ref, path); diagnostic != nil {
					return "", nil, diagnostic
				}
			case "a", "at":
				interactive = true
				value, diagnostic := decodePostInteractiveText(node, tag, path)
				if diagnostic != nil {
					return "", nil, diagnostic
				}
				// These labels are retained only in a post with resources. The
				// legacy text-command decoder never sees a/at nodes.
				text.WriteString(value)
				hasText = true
			default:
				return bad("unsupported_tag", path+".tag")
			}
		}
		if hasText {
			lines = append(lines, text.String())
		}
	}
	if len(refs) == 0 && interactive {
		return bad("interactive_only", contentPath)
	}
	if len(refs) == 0 && strings.TrimSpace(strings.Join(lines, "\n")) == "" {
		return bad("empty_content", contentPath)
	}
	return strings.Join(lines, "\n"), refs, nil
}

// Official SDK v3.11.0 service/im/v1/ext_model.go MessagePostText supplies
// un_escape; larksuite/feishu dt_message.py MessageText additionally supplies
// lines. Presentation metadata must never truncate text or change message_type.
func decodePostText(node map[string]json.RawMessage, path string) (string, *resourceNormalizationError) {
	var value string
	if !jsonString(node["text"], &value) {
		return "", &resourceNormalizationError{"invalid_field", path + ".text"}
	}
	unescape := false
	for _, key := range resourceFields(node) {
		raw := node[key]
		switch key {
		case "tag", "text":
		case "style":
			if !validPostStyle(raw) {
				return "", &resourceNormalizationError{"invalid_field", path + ".style"}
			}
		case "un_escape":
			if !jsonBoolean(raw, &unescape) {
				return "", &resourceNormalizationError{"invalid_field", path + ".un_escape"}
			}
		case "lines":
			var n int64
			if bytes.Equal(raw, []byte("null")) || json.Unmarshal(raw, &n) != nil || n < 0 {
				return "", &resourceNormalizationError{"invalid_field", path + ".lines"}
			}
		default:
			return "", &resourceNormalizationError{"unsupported_field", resourceFieldPath(path, key)}
		}
	}
	if unescape {
		value = html.UnescapeString(value)
	}
	return value, nil
}

func validPostStyle(raw json.RawMessage) bool {
	var styles []string
	if json.Unmarshal(raw, &styles) != nil || styles == nil {
		return false
	}
	for _, style := range styles {
		switch style {
		case "bold", "italic", "underline", "lineThrough":
		default:
			return false
		}
	}
	return true
}

func jsonBoolean(raw json.RawMessage, out *bool) bool {
	return (bytes.Equal(raw, []byte("true")) || bytes.Equal(raw, []byte("false"))) && json.Unmarshal(raw, out) == nil
}

func decodePostInteractiveText(node map[string]json.RawMessage, tag, path string) (string, *resourceNormalizationError) {
	values := make(map[string]string)
	unescape := false
	for _, key := range resourceFields(node) {
		raw := node[key]
		switch {
		case key == "style":
			if !validPostStyle(raw) {
				return "", &resourceNormalizationError{"invalid_field", path + ".style"}
			}
		case key == "un_escape" && tag == "a":
			if !jsonBoolean(raw, &unescape) {
				return "", &resourceNormalizationError{"invalid_field", path + ".un_escape"}
			}
		case key == "tag" || tag == "a" && (key == "text" || key == "href") || tag == "at" && (key == "user_id" || key == "user_name" || key == "text"):
			var value string
			if !jsonString(raw, &value) {
				return "", &resourceNormalizationError{"invalid_field", resourceFieldPath(path, key)}
			}
			values[key] = value
		default:
			return "", &resourceNormalizationError{"unsupported_field", resourceFieldPath(path, key)}
		}
	}
	if tag == "a" {
		if values["href"] == "" {
			return "", &resourceNormalizationError{"invalid_field", path + ".href"}
		}
		label, exists := values["text"]
		if !exists {
			return "", &resourceNormalizationError{"invalid_field", path + ".text"}
		}
		if unescape {
			label = html.UnescapeString(label)
		}
		// Plain display text; never fetch the href or extract resources from it.
		return label + " (" + values["href"] + ")", nil
	}
	if values["user_id"] == "" {
		return "", &resourceNormalizationError{"invalid_field", path + ".user_id"}
	}
	label := values["user_name"]
	if label == "" {
		label = values["text"]
	}
	if label == "" {
		label = values["user_id"]
	}
	return "@" + label, nil
}

// Mask code before passing markdown to the SDK's resource extractor. The SDK
// already tests triple-backtick fences; additionally protect tilde/long fences,
// unclosed fences, indented code and matched inline code. Only fence state is
// carried across md nodes; inline lookahead is bounded to the current md text.
// This mask is never returned as Content: all original text stays intact.
type markdownResourceState struct {
	fence       byte
	fenceLength int
}

var postMarkdownInteractivePattern = regexp.MustCompile(`\[[^\]\r\n]*\]\([^)\r\n]*\)|<at(?:\s|>)`)

func (state *markdownResourceState) outsideCode(value string) string {
	// Precompute the next run of exactly the same backtick length. This keeps
	// lookahead linear and prevents an unmatched delimiter from masking text.
	// Backslashes inside a matched code span do not escape its closing run.
	closers := markdownInlineClosers(value)
	inlineLength, offset := 0, 0
	var visible strings.Builder
	for _, line := range strings.SplitAfter(value, "\n") {
		lineStart := offset
		offset += len(line)
		trimmed := strings.TrimLeft(line, " ")
		indent := len(line) - len(trimmed)
		run := 0
		if len(trimmed) > 0 && (trimmed[0] == '`' || trimmed[0] == '~') {
			for run < len(trimmed) && trimmed[run] == trimmed[0] {
				run++
			}
		}
		if state.fence != 0 {
			if indent <= 3 && run >= state.fenceLength && trimmed[0] == state.fence && strings.TrimSpace(trimmed[run:]) == "" {
				state.fence = 0
				state.fenceLength = 0
			}
			visible.WriteByte('\n')
			continue
		}
		if inlineLength == 0 && indent <= 3 && run >= 3 && (trimmed[0] != '`' || !strings.Contains(trimmed[run:], "`")) {
			state.fence = trimmed[0]
			state.fenceLength = run
			visible.WriteByte('\n')
			continue
		}
		if inlineLength == 0 && (indent >= 4 || strings.HasPrefix(line, "\t")) {
			visible.WriteByte('\n')
			continue
		}
		for i := 0; i < len(line); {
			if inlineLength == 0 && line[i] == '\\' && i+1 < len(line) {
				// Escaped ! cannot introduce an image; escaped backticks cannot
				// introduce code. Consume escaped backslashes as a pair as well.
				if line[i+1] == '!' || line[i+1] == '`' || line[i+1] == '\\' {
					visible.WriteString("  ")
					i += 2
					continue
				}
			}
			if line[i] == '`' {
				n := 1
				for i+n < len(line) && line[i+n] == '`' {
					n++
				}
				if inlineLength == 0 {
					if _, matched := closers[lineStart+i]; matched {
						inlineLength = n
					}
				} else if inlineLength == n {
					inlineLength = 0
				}
				visible.WriteByte(' ')
				i += n
				continue
			}
			if inlineLength == 0 {
				visible.WriteByte(line[i])
			} else if line[i] == '\n' {
				visible.WriteByte('\n')
			} else {
				visible.WriteByte(' ')
			}
			i++
		}
	}
	return visible.String()
}

func markdownInlineClosers(value string) map[int]int {
	type tickRun struct{ start, length int }
	var runs []tickRun
	for i := 0; i < len(value); {
		if value[i] != '`' {
			i++
			continue
		}
		start := i
		for i < len(value) && value[i] == '`' {
			i++
		}
		runs = append(runs, tickRun{start, i - start})
	}
	next := make(map[int]int)
	closers := make(map[int]int)
	for i := len(runs) - 1; i >= 0; i-- {
		run := runs[i]
		if close, ok := next[run.length]; ok {
			closers[run.start] = close
		}
		next[run.length] = run.start
	}
	return closers
}

func jsonString(raw json.RawMessage, out *string) bool {
	return len(raw) > 0 && raw[0] == '"' && json.Unmarshal(raw, out) == nil
}

// Reject duplicate object keys, invalid UTF-8, trailing values, and excessive
// nesting before either the legacy text-post decoder or attachment decoder runs.
func unambiguousJSON(raw []byte) bool {
	if !utf8.Valid(raw) {
		return false
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	// Validate JSON syntax without prematurely converting numbers to float64;
	// individual schema fields provide the finite/range checks and safe path.
	d.UseNumber()
	var walk func(int) bool
	walk = func(depth int) bool {
		if depth > 32 {
			return false
		}
		token, err := d.Token()
		if err != nil {
			return false
		}
		delim, ok := token.(json.Delim)
		if !ok {
			return true
		}
		switch delim {
		case '{':
			seen := map[string]bool{}
			for d.More() {
				token, err := d.Token()
				key, ok := token.(string)
				if err != nil || !ok || seen[key] {
					return false
				}
				seen[key] = true
				if !walk(depth + 1) {
					return false
				}
			}
		case '[':
			for d.More() {
				if !walk(depth + 1) {
					return false
				}
			}
		default:
			return false
		}
		_, err = d.Token()
		return err == nil
	}
	if !walk(0) {
		return false
	}
	_, err := d.Token()
	return err == io.EOF
}
