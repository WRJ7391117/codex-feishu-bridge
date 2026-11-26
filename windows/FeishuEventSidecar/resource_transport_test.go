package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	lark "github.com/larksuite/oapi-sdk-go/v3"
)

type resourceHTTPMock func(*http.Request) (*http.Response, error)

func (mock resourceHTTPMock) Do(r *http.Request) (*http.Response, error) { return mock(r) }
func resourceResponse(code int, body string) *http.Response {
	return &http.Response{StatusCode: code, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: io.NopCloser(strings.NewReader(body)), ContentLength: int64(len(body))}
}
func resourceTestClient(mock resourceHTTPMock) *lark.Client {
	// Token cache disabled: the SDK invokes only the supplied in-memory HTTP mock.
	return lark.NewClient("fixture-app", "fixture-secret", lark.WithEnableTokenCache(false), lark.WithLogger(discardLogger{}), lark.WithHttpClient(resourceHTTPClient{base: mock}))
}
func downloadCommand(t *testing.T, kind string) inputCommand {
	t.Helper()
	return inputCommand{Type: "resource_download", RequestID: strings.Repeat("a", 32), MessageID: "om_fixture", FileKey: "file_fixture", ResourceKind: kind, OutputPath: filepath.Join(t.TempDir(), "unique.download")}
}
func sendCommand(t *testing.T, kind string, data []byte) inputCommand {
	t.Helper()
	path := filepath.Join(t.TempDir(), "payload.bin")
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	return inputCommand{Type: "resource_send", RequestID: strings.Repeat("a", 32), RecipientID: "ou_owner", Path: path, FileName: "资料.bin", ResourceKind: kind, IdempotencyKey: "stable-uuid"}
}

type countedResourceReader struct {
	remaining, read int64
	closed          bool
	fail            bool
}

func (reader *countedResourceReader) Read(p []byte) (int, error) {
	if reader.remaining == 0 {
		if reader.fail {
			return 0, errors.New("sensitive read error")
		}
		return 0, io.EOF
	}
	n := min(int64(len(p)), reader.remaining)
	clear(p[:n])
	reader.remaining -= n
	reader.read += n
	return int(n), nil
}
func (reader *countedResourceReader) Close() error { reader.closed = true; return nil }

func TestResourceDownloadBoundariesAndCleanup(t *testing.T) {
	for _, kind := range []string{"image", "file", "audio"} {
		for _, delta := range []int64{-1, 0, 1} {
			t.Run(kind+string(rune('1'+delta)), func(t *testing.T) {
				command := downloadCommand(t, kind)
				reader := &countedResourceReader{remaining: resourceLimit(kind) + delta}
				transport := &resourceTransport{download: func(ctx context.Context, id, key, actualKind string) (io.ReadCloser, error) {
					deadline, ok := ctx.Deadline()
					if !ok || time.Until(deadline) > resourceTimeout {
						t.Error("missing bounded context")
					}
					if id != command.MessageID || key != command.FileKey || actualKind != kind {
						t.Error("download contract changed")
					}
					return reader, nil
				}}
				got, ok := transport.process(context.Background(), command)
				if !ok || !reader.closed {
					t.Fatal("not handled or reader leaked", got)
				}
				if delta <= 0 {
					info, err := os.Stat(command.OutputPath)
					if got.State != "succeeded" || err != nil || info.Size() != resourceLimit(kind)+delta {
						t.Fatal(got, err)
					}
				} else {
					if got.ErrorStage != "size_limit" || reader.read > resourceLimit(kind)+1 {
						t.Fatal(got, reader.read)
					}
					if _, err := os.Stat(command.OutputPath); !os.IsNotExist(err) {
						t.Fatal("oversize output not removed", err)
					}
				}
			})
		}
	}
}

func TestResourceDownloadFailureAndCreateNew(t *testing.T) {
	for _, failure := range []string{"api", "io", "empty", "cancel"} {
		t.Run(failure, func(t *testing.T) {
			command := downloadCommand(t, "file")
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			transport := &resourceTransport{download: func(context.Context, string, string, string) (io.ReadCloser, error) {
				switch failure {
				case "api":
					return nil, &replyFailure{stage: "download_api", code: 234, diagnostic: "secret"}
				case "io":
					return &countedResourceReader{remaining: 8, fail: true}, nil
				case "cancel":
					cancel()
					return io.NopCloser(strings.NewReader("private-bytes")), nil
				default:
					return io.NopCloser(strings.NewReader("")), nil
				}
			}}
			got, _ := transport.process(ctx, command)
			if got.State != "failed" {
				t.Fatal(got)
			}
			if _, err := os.Stat(command.OutputPath); !os.IsNotExist(err) {
				t.Fatal("failed download left a file", err)
			}
			encoded, _ := json.Marshal(got)
			for _, secret := range []string{"secret", "private-bytes", command.OutputPath, command.FileKey} {
				if strings.Contains(string(encoded), secret) {
					t.Fatal("result leaked data")
				}
			}
		})
	}
	command := downloadCommand(t, "file")
	if err := os.WriteFile(command.OutputPath, []byte("keep-existing"), 0600); err != nil {
		t.Fatal(err)
	}
	transport := &resourceTransport{download: func(context.Context, string, string, string) (io.ReadCloser, error) {
		t.Fatal("existing target reached API")
		return nil, nil
	}}
	got, _ := transport.process(context.Background(), command)
	data, err := os.ReadFile(command.OutputPath)
	if got.ErrorStage != "path" || err != nil || string(data) != "keep-existing" {
		t.Fatal(got, err)
	}
}

func TestResourcePathsRejectTraversalDevicesDirectoriesAndSymlinks(t *testing.T) {
	root := t.TempDir()
	for _, path := range []string{"relative.download", filepath.Join(root, "x") + string(filepath.Separator) + ".." + string(filepath.Separator) + "escape", root, filepath.Join(root, "missing", "file"), filepath.Join(root, "NUL"), filepath.Join(root, "CON.txt"), filepath.Join(root, "stream:payload"), filepath.Join(root, "trailing."), `\\server\share\file`, `\\?\C:\secret`, `C:relative`} {
		if file, err := openSecureResource(path, true); err == nil {
			file.finish(false)
			t.Fatalf("accepted unsafe path %q", path)
		}
	}
	target := filepath.Join(root, "target")
	if err := os.Mkdir(target, 0700); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(root, "link")
	if err := os.Symlink(target, link); err != nil {
		t.Skipf("OS does not permit symlink creation: %v", err)
	}
	if file, err := openSecureResource(filepath.Join(link, "new.download"), true); err == nil {
		file.finish(false)
		t.Fatal("symlink ancestor accepted")
	}
	source := filepath.Join(target, "source")
	if err := os.WriteFile(source, []byte("do-not-touch"), 0600); err != nil {
		t.Fatal(err)
	}
	leaf := filepath.Join(root, "leaf")
	if err := os.Symlink(source, leaf); err != nil {
		t.Fatal(err)
	}
	for _, create := range []bool{false, true} {
		if file, err := openSecureResource(leaf, create); err == nil {
			file.finish(false)
			t.Fatal("symlink leaf accepted")
		}
	}
	data, _ := os.ReadFile(source)
	if string(data) != "do-not-touch" {
		t.Fatal("symlink target modified")
	}
}

func TestResourceHTTPStreamCapBeforeSDKBuffering(t *testing.T) {
	for _, knownLength := range []bool{false, true} {
		reader := &countedResourceReader{remaining: imageResourceLimit + 1}
		client := resourceTestClient(func(req *http.Request) (*http.Response, error) {
			if req.URL.Path != "/open-apis/im/v1/messages/om_fixture/resources/file_fixture" || req.URL.Query().Get("type") != "image" {
				t.Error("SDK download API mismatch", req.URL)
			}
			length := int64(-1)
			if knownLength {
				length = imageResourceLimit + 1
			}
			return &http.Response{StatusCode: 200, Header: make(http.Header), Body: reader, ContentLength: length}, nil
		})
		command := downloadCommand(t, "image")
		got, _ := newResourceTransport(client).process(context.Background(), command)
		if got.ErrorStage != "size_limit" || !reader.closed || reader.read > imageResourceLimit+1 {
			t.Fatal(got, reader.read, reader.closed)
		}
		if knownLength && reader.read != 0 {
			t.Fatal("read content known to be oversized")
		}
		if _, err := os.Stat(command.OutputPath); !os.IsNotExist(err) {
			t.Fatal("SDK overflow left output")
		}
	}
}

func TestResourceSDKDownloadAndSafeAPIFailure(t *testing.T) {
	for _, kind := range []string{"image", "file", "audio"} {
		client := resourceTestClient(func(req *http.Request) (*http.Response, error) {
			want := "file"
			if kind == "image" {
				want = "image"
			}
			if req.URL.Query().Get("type") != want {
				t.Error("bad resource query type")
			}
			return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": []string{"application/octet-stream"}}, Body: io.NopCloser(strings.NewReader("binary-secret")), ContentLength: 13}, nil
		})
		command := downloadCommand(t, kind)
		got, _ := newResourceTransport(client).process(context.Background(), command)
		body, err := os.ReadFile(command.OutputPath)
		if got.State != "succeeded" || err != nil || string(body) != "binary-secret" {
			t.Fatal(got, err)
		}
		encoded, _ := json.Marshal(got)
		if strings.Contains(string(encoded), "binary-secret") {
			t.Fatal("binary in stdout result")
		}
	}
	client := resourceTestClient(func(*http.Request) (*http.Response, error) {
		return resourceResponse(400, `{"code":99991672,"msg":"secret access token"}`), nil
	})
	got, _ := newResourceTransport(client).process(context.Background(), downloadCommand(t, "file"))
	if got.ErrorStage != "download_api" || got.ErrorCode != 99991672 || got.ErrorDiagnostic != "" {
		t.Fatal(got)
	}
}

func opusFixture() []byte {
	page := make([]byte, 28+19)
	copy(page, "OggS")
	page[5] = 2
	page[26] = 1
	page[27] = 19
	copy(page[28:], "OpusHead")
	page[36] = 1
	page[37] = 2
	return page
}

func TestResourceSendSDKUploadCodecAndIdempotency(t *testing.T) {
	for _, test := range []struct {
		kind, wantType string
		data           []byte
	}{
		{"image", "image", []byte("\x89PNG\r\n\x1a\nfixture")},
		{"file", "file", []byte("plain text fixture")},
		{"audio", "audio", opusFixture()},
		{"audio", "file", []byte("RIFFfixtureWAVE")},
		{"audio", "file", []byte("OpusHead-not-ogg")},
	} {
		t.Run(test.kind+test.wantType, func(t *testing.T) {
			uploads, sends := 0, 0
			command := sendCommand(t, test.kind, test.data)
			client := resourceTestClient(func(req *http.Request) (*http.Response, error) {
				if req.Method != "POST" {
					t.Fatal("unexpected method")
				}
				if strings.HasSuffix(req.URL.Path, "/messages") {
					sends++
					var body map[string]string
					if err := json.NewDecoder(req.Body).Decode(&body); err != nil {
						t.Fatal(err)
					}
					if body["uuid"] != command.IdempotencyKey || body["receive_id"] != command.RecipientID || body["msg_type"] != test.wantType || req.URL.Query().Get("receive_id_type") != "open_id" {
						t.Fatal(body, req.URL)
					}
					field := "file_key"
					if test.wantType == "image" {
						field = "image_key"
					}
					if body["content"] != `{"`+field+`":"uploaded_key"}` {
						t.Fatal(body)
					}
					return resourceResponse(200, `{"code":0,"data":{"message_id":"om_sent"}}`), nil
				}
				uploads++
				if err := req.ParseMultipartForm(1 << 20); err != nil {
					t.Fatal(err)
				}
				defer req.MultipartForm.RemoveAll()
				field := "file"
				if test.wantType == "image" {
					field = "image"
					if !strings.HasSuffix(req.URL.Path, "/images") || req.FormValue("image_type") != "message" {
						t.Fatal(req.URL)
					}
				} else {
					wantFileType := "stream"
					if test.wantType == "audio" {
						wantFileType = "opus"
					}
					if !strings.HasSuffix(req.URL.Path, "/files") || req.FormValue("file_type") != wantFileType || req.FormValue("file_name") != command.FileName {
						t.Fatal(req.URL, req.Form)
					}
				}
				file, _, err := req.FormFile(field)
				if err != nil {
					t.Fatal(err)
				}
				body, err := io.ReadAll(file)
				file.Close()
				if err != nil || !bytes.Equal(body, test.data) {
					t.Fatal("upload bytes mismatch", err)
				}
				return resourceResponse(200, `{"code":0,"data":{"file_key":"uploaded_key","image_key":"uploaded_key"}}`), nil
			})
			transport := newResourceTransport(client)
			for i := 0; i < 2; i++ {
				got, ok := processCommand(context.Background(), command, nil, nil, nil, nil, &taskCardTransport{resources: transport})
				if !ok || got.State != "succeeded" || got.MessageID != "om_sent" {
					t.Fatal(got)
				}
			}
			if uploads != 1 || sends != 1 {
				t.Fatal("duplicate upload/send", uploads, sends)
			}
			command.RecipientID = "ou_other"
			got, _ := transport.process(context.Background(), command)
			if got.ErrorStage != "idempotency_conflict" || uploads != 1 || sends != 1 {
				t.Fatal(got, uploads, sends)
			}
		})
	}
}

func TestResourceSendRetryAndConcurrentReplay(t *testing.T) {
	command := sendCommand(t, "file", []byte("content"))
	uploads, sends := 0, 0
	transport := &resourceTransport{
		upload: func(context.Context, string, string, io.Reader) (string, error) { uploads++; return "upload_key", nil },
		send: func(ctx context.Context, recipient, kind, key, uuid string) (string, error) {
			sends++
			if sends == 1 {
				return "", &replyFailure{stage: "send_transport"}
			}
			return "om_retry", nil
		},
	}
	got, _ := transport.process(context.Background(), command)
	if got.ErrorStage != "send_transport" {
		t.Fatal(got)
	}
	var workers sync.WaitGroup
	results := make(chan outputEvent, 8)
	for i := 0; i < 8; i++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			got, _ := transport.process(context.Background(), command)
			results <- got
		}()
	}
	workers.Wait()
	close(results)
	for got := range results {
		if got.State != "succeeded" || got.MessageID != "om_retry" {
			t.Fatal(got)
		}
	}
	if uploads != 1 || sends != 2 {
		t.Fatal(uploads, sends)
	}
	if err := os.WriteFile(command.Path, []byte("changed"), 0600); err != nil {
		t.Fatal(err)
	}
	got, _ = transport.process(context.Background(), command)
	if got.ErrorStage != "idempotency_conflict" {
		t.Fatal(got)
	}
}

func TestResourceSendValidationAndFailures(t *testing.T) {
	command := sendCommand(t, "file", []byte("content"))
	for _, mutate := range []func(*inputCommand){
		func(c *inputCommand) { c.ResourceKind = "media" }, func(c *inputCommand) { c.RecipientID = "" },
		func(c *inputCommand) { c.FileName = "../private" }, func(c *inputCommand) { c.FileName = "" },
		func(c *inputCommand) { c.IdempotencyKey = "" }, func(c *inputCommand) { c.Path = "relative" },
		func(c *inputCommand) { c.Path = filepath.Dir(c.Path) },
	} {
		bad := command
		mutate(&bad)
		transport := &resourceTransport{upload: func(context.Context, string, string, io.Reader) (string, error) {
			t.Fatal("validation reached upload")
			return "", nil
		}, send: func(context.Context, string, string, string, string) (string, error) {
			t.Fatal("validation reached send")
			return "", nil
		}}
		got, _ := transport.process(context.Background(), bad)
		if got.State != "failed" {
			t.Fatal(got)
		}
	}
	for _, size := range []int64{0, imageResourceLimit + 1, fileResourceLimit + 1} {
		kind := "file"
		if size == imageResourceLimit+1 {
			kind = "image"
		}
		bad := sendCommand(t, kind, []byte("x"))
		if err := os.Truncate(bad.Path, size); err != nil {
			t.Fatal(err)
		}
		transport := &resourceTransport{upload: func(context.Context, string, string, io.Reader) (string, error) {
			t.Fatal("oversize upload")
			return "", nil
		}, send: func(context.Context, string, string, string, string) (string, error) { return "", nil }}
		got, _ := transport.process(context.Background(), bad)
		if got.ErrorStage != "size_limit" {
			t.Fatal(got)
		}
	}
	for _, stage := range []string{"upload", "send"} {
		client := resourceTestClient(func(req *http.Request) (*http.Response, error) {
			if stage == "send" && strings.HasSuffix(req.URL.Path, "/files") {
				return resourceResponse(200, `{"code":0,"data":{"file_key":"uploaded_key"}}`), nil
			}
			return resourceResponse(400, `{"code":999,"msg":"secret server response"}`), nil
		})
		got, _ := newResourceTransport(client).process(context.Background(), command)
		if got.ErrorStage != stage+"_api" || got.ErrorCode != 999 {
			t.Fatal(got)
		}
	}
}

func TestResourceOpusMagicRejectsTruncatedOrOtherCodecs(t *testing.T) {
	valid := opusFixture()
	if !isOggOpus(valid) {
		t.Fatal("valid opus rejected")
	}
	for i := 0; i < len(valid); i++ {
		if isOggOpus(valid[:i]) {
			t.Fatal("truncated opus accepted", i)
		}
	}
	for _, index := range []int{0, 4, 5, 18, 26, 27, 28, 36, 37, 46} {
		bad := append([]byte(nil), valid...)
		bad[index] = 255
		if isOggOpus(bad) {
			t.Fatal("bad opus header accepted", index)
		}
	}
}

func TestResourceProtocolMissingTransportAndMalformedRequestID(t *testing.T) {
	command := downloadCommand(t, "image")
	got, ok := processCommand(context.Background(), command, nil, nil, nil, nil)
	if !ok || got.ErrorStage != "unavailable" {
		t.Fatal(got)
	}
	command.RequestID = "invalid"
	if _, ok := processCommand(context.Background(), command, nil, nil, nil, nil); ok {
		t.Fatal("invalid request id accepted")
	}
}

func TestResourceConcurrencyLeavesNormalCommandsResponsive(t *testing.T) {
	// Pipe stdin remains open until a normal command is handled, so EOF does
	// not cancel workers before the concurrency behavior has been observed.
	input, writer := io.Pipe()
	defer input.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	started := make(chan struct{}, 2)
	transport := &resourceTransport{download: func(ctx context.Context, _, _, _ string) (io.ReadCloser, error) {
		started <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	}}
	oldStdout := os.Stdout
	readOut, writeOut, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	os.Stdout = writeOut
	defer func() { os.Stdout = oldStdout; writeOut.Close(); readOut.Close() }()
	outputDone := make(chan []byte, 1)
	go func() { data, _ := io.ReadAll(readOut); outputDone <- data }()
	done := make(chan struct{})
	go func() {
		defer close(done)
		consumeCommands(ctx, json.NewDecoder(input), nil, func(context.Context, string, string, string) error { return nil }, nil, nil, cancel, &taskCardTransport{resources: transport})
	}()
	encode := json.NewEncoder(writer)
	for i := 0; i < 2; i++ {
		command := downloadCommand(t, "file")
		if err := encode.Encode(command); err != nil {
			t.Fatal(err)
		}
		select {
		case <-started:
		case <-time.After(5 * time.Second):
			t.Fatal("resource did not start")
		}
	}
	third := downloadCommand(t, "file")
	third.RequestID = strings.Repeat("b", 32)
	if err := encode.Encode(third); err != nil {
		t.Fatal(err)
	}
	if err := encode.Encode(inputCommand{Type: "send_p2p", RequestID: strings.Repeat("c", 32), RecipientID: "ou_owner", Text: "progress", IdempotencyKey: "progress-uuid"}); err != nil {
		t.Fatal(err)
	}
	writer.Close()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("consumer failed to cancel resource workers")
	}
	writeOut.Close()
	data := <-outputDone
	decoder := json.NewDecoder(bytes.NewReader(data))
	busy, progress, results := false, false, 0
	for {
		var event outputEvent
		if err := decoder.Decode(&event); err == io.EOF {
			break
		} else if err != nil {
			t.Fatal("interleaved JSON", err)
		}
		results++
		if event.RequestID == third.RequestID && event.ErrorStage == "busy" {
			busy = true
		}
		if event.Operation == "send_p2p" && event.State == "succeeded" {
			progress = true
		}
	}
	if !busy || !progress || results != 4 {
		t.Fatalf("busy=%v progress=%v results=%d", busy, progress, results)
	}
}
