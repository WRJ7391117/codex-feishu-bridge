package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	lark "github.com/larksuite/oapi-sdk-go/v3"
	larkcore "github.com/larksuite/oapi-sdk-go/v3/core"
	larkim "github.com/larksuite/oapi-sdk-go/v3/service/im/v1"
)

const imageResourceLimit int64 = 20 << 20
const fileResourceLimit int64 = 50 << 20
const resourceTimeout = 120 * time.Second
const maxResourceSends = 1024

var errResourceLimit = errors.New("resource size limit exceeded")

type resourceTransport struct {
	download func(context.Context, string, string, string) (io.ReadCloser, error)
	upload   func(context.Context, string, string, io.Reader) (string, error)
	send     func(context.Context, string, string, string, string) (string, error)
	mu       sync.Mutex
	sends    map[string]*resourceSendRecord
}

type resourceSendRecord struct {
	fingerprint [32]byte
	key         string
	messageID   string
}

func resourceLimit(kind string) int64 {
	switch kind {
	case "image":
		return imageResourceLimit
	case "file", "audio":
		return fileResourceLimit
	default:
		return 0
	}
}

// The SDK buffers response bodies internally. Bound the network stream before
// that buffering, on the same authenticated bot client. No raw bytes are logged.
type resourceHTTPClient struct{ base larkcore.HttpClient }
type resourceLimitContextKey struct{}

func (client resourceHTTPClient) Do(req *http.Request) (*http.Response, error) {
	base := client.base
	if base == nil {
		base = &http.Client{Timeout: resourceTimeout, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	}
	resp, err := base.Do(req)
	if err != nil || resp == nil {
		return resp, err
	}
	if req.Method == http.MethodGet && strings.HasPrefix(req.URL.Path, "/open-apis/im/v1/messages/") && strings.Contains(req.URL.Path, "/resources/") {
		limit, _ := req.Context().Value(resourceLimitContextKey{}).(int64)
		if limit <= 0 {
			limit = fileResourceLimit
		}
		if resp.StatusCode != http.StatusOK {
			limit = 64 * 1024
		}
		if resp.Body == nil {
			return nil, &replyFailure{stage: "download_transport"}
		}
		if resp.ContentLength > limit {
			_ = resp.Body.Close()
			return nil, errResourceLimit
		}
		resp.Body = &cappedResourceBody{ReadCloser: resp.Body, remaining: limit, ctx: req.Context()}
	}
	return resp, nil
}

type cappedResourceBody struct {
	io.ReadCloser
	remaining int64
	ctx       context.Context
}

func (body *cappedResourceBody) Read(p []byte) (int, error) {
	if err := body.ctx.Err(); err != nil {
		return 0, err
	}
	if len(p) == 0 {
		return 0, nil
	}
	if body.remaining == 0 {
		var extra [1]byte
		n, err := body.ReadCloser.Read(extra[:])
		if n != 0 {
			return 0, errResourceLimit
		}
		return 0, err
	}
	if int64(len(p)) > body.remaining {
		p = p[:body.remaining]
	}
	n, err := body.ReadCloser.Read(p)
	body.remaining -= int64(n)
	return n, err
}

func newResourceTransport(client *lark.Client) *resourceTransport {
	return &resourceTransport{
		sends: make(map[string]*resourceSendRecord),
		download: func(ctx context.Context, messageID, key, kind string) (io.ReadCloser, error) {
			apiKind := "file"
			if kind == "image" {
				apiKind = "image"
			}
			ctx = context.WithValue(ctx, resourceLimitContextKey{}, resourceLimit(kind))
			resp, err := client.Im.MessageResource.Get(ctx, larkim.NewGetMessageResourceReqBuilder().MessageId(messageID).FileKey(key).Type(apiKind).Build(), larkcore.WithFileDownload())
			if err != nil {
				return nil, resourceSDKError("download", err)
			}
			if resp == nil {
				return nil, &replyFailure{stage: "download_transport"}
			}
			if !resp.Success() {
				return nil, &replyFailure{stage: "download_api", code: resp.Code}
			}
			if resp.ApiResp == nil || resp.StatusCode != http.StatusOK {
				code := 0
				if resp.ApiResp != nil {
					code = resp.StatusCode
				}
				return nil, &replyFailure{stage: "download_api", code: code}
			}
			if resp.File == nil {
				return nil, &replyFailure{stage: "download_encoding"}
			}
			if closer, ok := resp.File.(io.ReadCloser); ok {
				return closer, nil
			}
			return io.NopCloser(resp.File), nil
		},
		upload: func(ctx context.Context, kind, name string, reader io.Reader) (string, error) {
			if kind == "image" {
				resp, err := client.Im.Image.Create(ctx, larkim.NewCreateImageReqBuilder().Body(larkim.NewCreateImageReqBodyBuilder().ImageType("message").Image(reader).Build()).Build())
				if err != nil {
					return "", resourceSDKError("upload", err)
				}
				if resp == nil {
					return "", &replyFailure{stage: "upload_transport"}
				}
				if !resp.Success() {
					return "", &replyFailure{stage: "upload_api", code: resp.Code}
				}
				if resp.Data == nil || resp.Data.ImageKey == nil || !validResourceID(*resp.Data.ImageKey) {
					return "", &replyFailure{stage: "upload_encoding"}
				}
				return *resp.Data.ImageKey, nil
			}
			fileType := "stream"
			if kind == "audio" {
				fileType = "opus"
			}
			resp, err := client.Im.File.Create(ctx, larkim.NewCreateFileReqBuilder().Body(larkim.NewCreateFileReqBodyBuilder().FileType(fileType).FileName(name).File(reader).Build()).Build())
			if err != nil {
				return "", resourceSDKError("upload", err)
			}
			if resp == nil {
				return "", &replyFailure{stage: "upload_transport"}
			}
			if !resp.Success() {
				return "", &replyFailure{stage: "upload_api", code: resp.Code}
			}
			if resp.Data == nil || resp.Data.FileKey == nil || !validResourceID(*resp.Data.FileKey) {
				return "", &replyFailure{stage: "upload_encoding"}
			}
			return *resp.Data.FileKey, nil
		},
		send: func(ctx context.Context, recipient, kind, key, uuid string) (string, error) {
			field := "file_key"
			if kind == "image" {
				field = "image_key"
			}
			content, _ := json.Marshal(map[string]string{field: key})
			resp, err := client.Im.Message.Create(ctx, larkim.NewCreateMessageReqBuilder().ReceiveIdType("open_id").Body(larkim.NewCreateMessageReqBodyBuilder().ReceiveId(recipient).MsgType(kind).Content(string(content)).Uuid(uuid).Build()).Build())
			if err != nil {
				return "", resourceSDKError("send", err)
			}
			if resp == nil {
				return "", &replyFailure{stage: "send_transport"}
			}
			if !resp.Success() {
				return "", &replyFailure{stage: "send_api", code: resp.Code}
			}
			if resp.Data == nil || resp.Data.MessageId == nil || !validResourceID(*resp.Data.MessageId) {
				return "", &replyFailure{stage: "send_encoding"}
			}
			return *resp.Data.MessageId, nil
		},
	}
}

func resourceSDKError(stage string, err error) error {
	if errors.Is(err, errResourceLimit) {
		return &replyFailure{stage: "size_limit"}
	}
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return &replyFailure{stage: "timeout"}
	}
	return &replyFailure{stage: stage + "_transport"}
}

func (transport *resourceTransport) process(ctx context.Context, command inputCommand) (outputEvent, bool) {
	if command.Type != "resource_download" && command.Type != "resource_send" || !validOpaqueRequestID(command.RequestID) {
		return outputEvent{}, false
	}
	result := outputEvent{Type: "command_result", RequestID: command.RequestID, Operation: command.Type, State: "failed", ErrorStage: "validation"}
	if resourceLimit(command.ResourceKind) == 0 {
		return result, true
	}
	if transport == nil {
		result.ErrorStage = "unavailable"
		return result, true
	}
	ctx, cancel := context.WithTimeout(ctx, resourceTimeout)
	defer cancel()
	var err error
	if command.Type == "resource_download" {
		if !validResourceID(command.MessageID) || !validResourceID(command.FileKey) {
			return result, true
		}
		err = transport.downloadResource(ctx, command)
	} else {
		if !validResourceID(command.RecipientID) || len(command.RecipientID) > 256 || strings.TrimSpace(command.IdempotencyKey) == "" || len(command.IdempotencyKey) > 64 || !validResourceName(command.FileName) || strings.TrimSpace(command.FileName) == "" {
			return result, true
		}
		result.MessageID, err = transport.sendResource(ctx, command)
	}
	if err != nil {
		result.ErrorStage = "transport"
		var failure *replyFailure
		if errors.As(err, &failure) {
			result.ErrorStage, result.ErrorCode = failure.stage, failure.code
		}
		if ctx.Err() != nil {
			result.ErrorStage = "timeout"
		}
		return result, true
	}
	result.State, result.ErrorStage = "succeeded", ""
	return result, true
}

func (transport *resourceTransport) downloadResource(ctx context.Context, command inputCommand) (err error) {
	if transport.download == nil {
		return &replyFailure{stage: "unavailable"}
	}
	file, err := openSecureResource(command.OutputPath, true)
	if err != nil {
		return &replyFailure{stage: "path"}
	}
	keep := false
	defer func() {
		if closeErr := file.finish(keep); closeErr != nil && err == nil {
			err = &replyFailure{stage: "write"}
		}
	}()
	reader, err := transport.download(ctx, command.MessageID, command.FileKey, command.ResourceKind)
	if err != nil {
		if reader != nil {
			_ = reader.Close()
		}
		return err
	}
	if reader == nil {
		return &replyFailure{stage: "download_encoding"}
	}
	defer reader.Close()
	bounded := &cappedResourceBody{ReadCloser: reader, remaining: resourceLimit(command.ResourceKind), ctx: ctx}
	n, err := io.Copy(file.File, bounded)
	if errors.Is(err, errResourceLimit) {
		return &replyFailure{stage: "size_limit"}
	}
	if err != nil {
		return &replyFailure{stage: "download_io"}
	}
	if n == 0 {
		return &replyFailure{stage: "download_encoding"}
	}
	if err = file.Sync(); err != nil {
		return &replyFailure{stage: "write"}
	}
	if ctx.Err() != nil {
		return ctx.Err()
	}
	keep = true
	return nil
}

func (transport *resourceTransport) sendResource(ctx context.Context, command inputCommand) (string, error) {
	if transport.upload == nil || transport.send == nil {
		return "", &replyFailure{stage: "unavailable"}
	}
	file, err := openSecureResource(command.Path, false)
	if err != nil {
		return "", &replyFailure{stage: "path"}
	}
	defer file.finish(true)
	info, err := file.Stat()
	limit := resourceLimit(command.ResourceKind)
	if err != nil || !info.Mode().IsRegular() {
		return "", &replyFailure{stage: "path"}
	}
	if info.Size() == 0 || info.Size() > limit {
		return "", &replyFailure{stage: "size_limit"}
	}
	// Snapshot the validated handle under a read cap. Upload and fingerprint use
	// exactly these bytes, even if another process changes the source afterward.
	body, err := io.ReadAll(&cappedResourceBody{ReadCloser: file.File, remaining: limit, ctx: ctx})
	if errors.Is(err, errResourceLimit) {
		return "", &replyFailure{stage: "size_limit"}
	}
	if err != nil || int64(len(body)) != info.Size() {
		return "", &replyFailure{stage: "read"}
	}
	kind := command.ResourceKind
	if kind == "audio" && !isOggOpus(body) {
		kind = "file"
	}
	digest := sha256.Sum256(body)
	metadata, _ := json.Marshal([]string{command.RecipientID, command.ResourceKind, command.FileName})
	fingerprint := sha256.Sum256(append(metadata, digest[:]...))
	transport.mu.Lock()
	defer transport.mu.Unlock()
	if ctx.Err() != nil {
		return "", ctx.Err()
	}
	if transport.sends == nil {
		transport.sends = make(map[string]*resourceSendRecord)
	}
	record := transport.sends[command.IdempotencyKey]
	if record != nil && record.fingerprint != fingerprint {
		return "", &replyFailure{stage: "idempotency_conflict"}
	}
	if record == nil {
		if len(transport.sends) >= maxResourceSends {
			return "", &replyFailure{stage: "idempotency_capacity"}
		}
		record = &resourceSendRecord{fingerprint: fingerprint}
		transport.sends[command.IdempotencyKey] = record
	}
	if record.messageID != "" {
		return record.messageID, nil
	}
	if record.key == "" {
		record.key, err = transport.upload(ctx, kind, command.FileName, bytes.NewReader(body))
		if err != nil {
			record.key = ""
			return "", err
		}
		if !validResourceID(record.key) {
			record.key = ""
			return "", &replyFailure{stage: "upload_encoding"}
		}
	}
	id, err := transport.send(ctx, command.RecipientID, kind, record.key, command.IdempotencyKey)
	if err != nil {
		return "", err
	}
	if !validResourceID(id) {
		return "", &replyFailure{stage: "send_encoding"}
	}
	record.messageID = id
	return id, nil
}

// An Ogg BOS page whose first complete packet is an Opus identification header.
// Filename extensions alone never select native audio transport.
func isOggOpus(data []byte) bool {
	if len(data) < 28 || string(data[:4]) != "OggS" || data[4] != 0 || data[5]&3 != 2 || !bytes.Equal(data[18:22], []byte{0, 0, 0, 0}) {
		return false
	}
	segments := int(data[26])
	if segments == 0 || len(data) < 27+segments {
		return false
	}
	length := 0
	for _, segment := range data[27 : 27+segments] {
		length += int(segment)
		if segment == 255 {
			continue
		}
		start := 27 + segments
		if length < 19 || len(data) < start+length {
			return false
		}
		header := data[start : start+length]
		if string(header[:8]) != "OpusHead" || header[8] == 0 || header[8] > 15 || header[9] == 0 {
			return false
		}
		if header[18] == 0 {
			return length == 19 && header[9] <= 2
		}
		return length >= 21+int(header[9]) && header[19] > 0 && header[20] <= header[19]
	}
	return false
}

type secureResourceFile struct {
	*os.File
	finish func(keep bool) error
}

// Reject device/UNC paths, alternate data streams, lexical traversal and Windows
// path aliases. C# supplies absolute local paths and creates the parent folder.
func validResourcePath(path string) bool {
	if path == "" || !filepath.IsAbs(path) || strings.ContainsRune(path, 0) || filepath.Clean(path) != path {
		return false
	}
	volume := filepath.VolumeName(path)
	if strings.HasPrefix(volume, `\`) || strings.HasPrefix(path, "//") {
		return false
	}
	rest := strings.TrimPrefix(path, volume)
	if strings.Contains(rest, ":") {
		return false
	}
	for _, part := range strings.FieldsFunc(rest, func(r rune) bool { return r == '/' || r == '\\' }) {
		if strings.HasSuffix(part, ".") || strings.HasSuffix(part, " ") {
			return false
		}
		base := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
		if base == "CON" || base == "PRN" || base == "AUX" || base == "NUL" || base == "CONIN$" || base == "CONOUT$" || len(base) == 4 && (strings.HasPrefix(base, "COM") || strings.HasPrefix(base, "LPT")) && strings.ContainsRune("123456789¹²³", rune(base[3])) {
			return false
		}
	}
	return true
}
