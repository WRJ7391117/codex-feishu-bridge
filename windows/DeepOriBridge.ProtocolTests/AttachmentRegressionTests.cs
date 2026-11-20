using System.Text;
using System.Text.Json;
using DeepOriBridge.Services;

internal static class AttachmentRegressionTests
{
    internal static Task RunAsync(Action<bool, string> assert)
    {
        void Reject(Action action, string description)
        {
            InvalidOperationException? failure = null;
            try { action(); }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }
            assert(failure is not null, description);
            if (failure is not null)
                assert(failure.Message.Any(c => c is >= '\u4e00' and <= '\u9fff')
                    && !failure.Message.Contains("SECRET", StringComparison.Ordinal)
                    && failure.InnerException is null, description + " (safe Chinese error)");
        }

        IReadOnlyList<FeishuInputResource> Read(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return FeishuAttachments.ReadResources(document.RootElement);
        }

        assert(Read("{}").Count == 0 && Read("{\"resources\":[]}").Count == 0,
            "missing and empty resource arrays are text-only messages");
        // resource_error is deliberately interpreted by the runtime, not this helper.
        assert(Read("{\"resource_error\":\"handled upstream\"}").Count == 0, "resource_error belongs to the caller");
        string Diagnostic(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return FeishuAttachments.ReadDiagnostic(document.RootElement);
        }
        assert(Diagnostic("""{"resource_error_code":"invalid_field","resource_error_path":"post.content[0][1].un_escape"}""")
            == "invalid_field @ post.content[0][1].un_escape", "known schema diagnostics survive without raw input");
        assert(Diagnostic("""{"resource_error_code":"unsupported_field","resource_error_path":"post.content[0][1].unknown"}""")
            == "unsupported_field @ post.content[0][1].unknown", "unknown fields are represented by a fixed label");
        foreach (string invalidPath in new[] { "post.SECRET", "file_SECRET", "C:\\SECRET", "post.content[0]\nSECRET", new string('x', 161) })
            assert(Diagnostic(JsonSerializer.Serialize(new { resource_error_code = "invalid_field", resource_error_path = invalidPath }))
                == "invalid_field", "diagnostics never display arbitrary fields, paths or content");
        foreach (string invalid in new[] { "null", "[]", "{}", """{"resource_error_code":"SECRET","resource_error_path":"post"}""",
            """{"resource_error_code":3}""", """{"resource_error_code":null}""" })
            assert(Diagnostic(invalid) == "", "only fixed diagnostic codes are allowed");
        assert(Diagnostic("""{"resource_error_code":"resource_limit","resource_error_path":null}""") == "resource_limit",
            "malformed diagnostic location does not hide the safe error category");
        var parsed = Read("""
            {"resources":[{"kind":"image","key":"img_abc"},
             {"kind":"file","key":"file_abC-123_","name":"报告 中文🙂.pdf"},
             {"kind":"audio","key":"file_voice","name":"语音.opus"}]}
            """);
        assert(parsed.Count == 3 && parsed[0] == new FeishuInputResource("image", "img_abc", "")
            && parsed[1].Name == "报告 中文🙂.pdf" && parsed[2].Kind == "audio",
            "normalized resources preserve order, keys and Unicode names after JSON disposal");
        foreach (string json in new[]
        {
            "null", "[]", "{\"resources\":null}", "{\"resources\":{}}", "{\"resources\":true}",
            "{\"resources\":[null]}", "{\"resources\":[1]}", "{\"resources\":[{}]}",
            """{"resources":[{"kind":4,"key":"img_abc"}]}""",
            """{"resources":[{"kind":"image","key":42}]}""",
            """{"resources":[{"kind":"video","key":"file_abc"}]}""",
            """{"resources":[{"kind":"image","key":"file_abc"}]}""",
            """{"resources":[{"kind":"file","key":"img_abc"}]}""",
            """{"resources":[{"kind":"image","key":"img_ab"}]}""",
            """{"resources":[{"kind":"image","key":"img_SECRET/../bad"}]}""",
            """{"resources":[{"kind":"image","key":"img_中文名"}]}""",
            """{"resources":[{"kind":"image","key":"img_abc","name":null}]}""",
            """{"resources":[{"kind":"file","key":"file_abc","name":7}]}""",
            """{"resources":[{"kind":"file","key":"file_abc","name":"SECRET\n.pdf"}]}""",
            """{"resources":[{"kind":"file","key":"file_abc","name":"../SECRET/report.pdf"}]}""",
        }) Reject(() => Read(json), "malformed resource JSON is rejected");
        Reject(() => FeishuAttachments.ReadResources(default), "undefined JSON is rejected");

        string Resources(params object[] resources) => JsonSerializer.Serialize(new { resources });
        object Resource(string kind, string key = "", string name = "") => new
        {
            kind, key = key.Length == 0 ? (kind == "image" ? "img_abc" : "file_abc") : key, name,
        };
        object[] maximum = Enumerable.Range(0, 4).Select(_ => Resource("image"))
            .Concat(Enumerable.Range(0, 4).Select(i => Resource(i % 2 == 0 ? "file" : "audio"))).ToArray();
        assert(Read(Resources(maximum)).Count == 8, "four images plus four combined files/audio are allowed");
        Reject(() => Read(Resources(maximum.Append(Resource("image")).ToArray())), "fifth image is rejected");
        Reject(() => Read(Resources(maximum.Append(Resource("audio")).ToArray())), "fifth combined file/audio is rejected");
        Reject(() => Read(Resources(Enumerable.Repeat(Resource("audio"), 5).ToArray())), "five audio resources exceed the file quota");
        assert(Read(Resources(Resource("image", "img_" + new string('a', 512)))).Count == 1, "maximum key length is accepted");
        Reject(() => Read(Resources(Resource("file", "file_" + new string('a', 513)))), "oversized keys are rejected");
        Reject(() => Read(Resources(Resource("file", name: new string('中', 1025)))), "oversized names are rejected");

        DirectoryInfo directory = Directory.CreateTempSubdirectory("deepori-attachment-tests-");
        string ownedRoot = Path.GetFullPath(directory.FullName);
        var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedDirectories = new List<string>();
        var links = new List<(string Path, bool Directory)>();
        try
        {
            string Write(byte[] bytes, string extension = ".download", long? length = null)
            {
                string path = Path.Combine(ownedRoot, Guid.NewGuid().ToString("N") + extension);
                ownedFiles.Add(path);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(bytes);
                if (length is long size) stream.SetLength(size);
                return path;
            }

            CodexInputAttachment Download(byte[] bytes, string kind, string name, long? length = null)
            {
                string path = Write(bytes, length: length);
                CodexInputAttachment attachment = FeishuAttachments.ValidateDownloaded(path, new(kind, "", name));
                ownedFiles.Add(attachment.Path);
                assert(!File.Exists(path) && File.Exists(attachment.Path), "owned staging file is renamed");
                assert(Path.GetDirectoryName(attachment.Path) == ownedRoot && Path.IsPathFullyQualified(attachment.Path),
                    "normalized download stays in its generated directory with an absolute path");
                assert(Guid.TryParseExact(Path.GetFileNameWithoutExtension(attachment.Path), "N", out _),
                    "incoming labels never supply the generated basename");
                return attachment;
            }

            byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
            (byte[] Bytes, string Extension)[] signatures =
            [
                (png, ".png"), ([0xff, 0xd8, 0xff], ".jpg"), ("GIF87a"u8.ToArray(), ".gif"),
                ("GIF89a"u8.ToArray(), ".gif"), ("RIFF\0\0\0\0WEBP"u8.ToArray(), ".webp"),
            ];
            foreach (var signature in signatures)
            {
                var image = Download(signature.Bytes, "image", "../../SECRET/误导.exe");
                assert(image.Kind == "image" && Path.GetExtension(image.Path) == signature.Extension
                    && image.Name == "误导" + signature.Extension, "image magic determines format regardless of incoming name");
            }
            foreach (byte[] bytes in new[]
            {
                "not an image"u8.ToArray(), png[..7], new byte[] { 0xff, 0xd8 },
                "GIF88a"u8.ToArray(), "RIFF\0\0\0\0WAVE"u8.ToArray(), "RIFFWEBP"u8.ToArray(),
            }) Reject(() => Download(bytes, "image", "照片.png"), "spoofed and truncated image signatures are rejected");

            var fullImage = Download(png, "image", "边界.png", 20L * 1024 * 1024);
            assert(new FileInfo(fullImage.Path).Length == 20L * 1024 * 1024, "20 MiB image is allowed");
            Reject(() => Download(png, "image", "大.png", 20L * 1024 * 1024 + 1), "image over 20 MiB is rejected");
            var fullFile = Download([1], "file", "边界.pdf", 50L * 1024 * 1024);
            assert(new FileInfo(fullFile.Path).Length == 50L * 1024 * 1024, "50 MiB file is allowed");
            Reject(() => Download([1], "file", "大.pdf", 50L * 1024 * 1024 + 1), "file over 50 MiB is rejected");
            Reject(() => Download([1], "audio", "大.opus", 50L * 1024 * 1024 + 1), "audio shares the 50 MiB limit");
            foreach (string kind in new[] { "image", "file", "audio" })
                Reject(() => Download([], kind, kind == "file" ? "空.pdf" : ""), "empty downloads are rejected for every kind");

            string[] documents =
            [
                ".c", ".cpp", ".cs", ".csv", ".doc", ".docx", ".go", ".h", ".hpp", ".html", ".java",
                ".js", ".json", ".jsx", ".kt", ".kts", ".md", ".pdf", ".ppt", ".pptx", ".py", ".rs",
                ".rtf", ".sh", ".sql", ".swift", ".ts", ".tsx", ".txt", ".xls", ".xlsx", ".xml", ".yaml", ".yml", ".zsh",
            ];
            foreach (string extension in documents)
            {
                var document = Download([1], "file", "资料" + extension.ToUpperInvariant());
                assert(document.Kind == "file" && Path.GetExtension(document.Path) == extension,
                    "Mac document/code whitelist is accepted case-insensitively: " + extension);
            }
            foreach (string extension in new[] { ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav" })
            {
                var audio = Download([1], "file", "录音" + extension);
                assert(audio.Kind == "audio", "audio extension selects localAudio even in a file message: " + extension);
            }
            assert(Path.GetExtension(Download([1], "audio", "").Path) == ".opus", "nameless native audio defaults to opus");
            foreach (string name in new[] { "bad.exe", "bad.zip", "bad.7z", "bad.dll", "bad.mp4", "bad.png", "no-extension", "bad.pdf.exe", "bad.pdf:SECRET", "bad.pdf " })
                Reject(() => Download([1], "file", name), "unsupported or disguised file type is rejected");
            Reject(() => Download([1], "audio", "not-audio.pdf"), "document cannot be mislabeled as audio");
            Reject(() => Download([1], "video", "video.mp4"), "unknown download kind is rejected");
            foreach (string name in new[] { "C:\\SECRET\\..\\报告.pdf", "/SECRET/report.pdf", "../SECRET.pdf", "folder/report.pdf", "C:SECRET.pdf" })
            {
                Reject(() => Download([1], "file", name), "incoming document names containing paths are rejected");
                Reject(() => Download([1], "audio", name), "incoming audio names containing paths are rejected");
            }
            var unicode = Download(Encoding.UTF8.GetBytes("正文"), "file", "报告 中文🙂.pdf");
            assert(unicode.Name == "报告 中文🙂.pdf", "Unicode file label survives normalization with an empty resource key");
            assert(Download([1], "file", "CON.pdf").Name == "CON.pdf",
                "reserved incoming display name never becomes a Windows device path");

            string regular = Write([1, 2, 3], ".pdf");
            FeishuAttachments.ValidateLocalFile(regular, 3);
            assert(FeishuAttachments.ValidateDownloaded(regular, new("file", "", "正文.pdf")).Path == regular,
                "already normalized regular files are not renamed");
            string executable = Write([1], ".exe");
            Reject(() => FeishuAttachments.ValidateDownloaded(executable, new("file", "", "fake.pdf")),
                "incoming label cannot disguise an executable local extension");
            string mismatch = Write(png, ".jpg");
            Reject(() => FeishuAttachments.ValidateDownloaded(mismatch, new("image", "", "图.png")),
                "non-staging files with mismatched image extensions are not renamed");
            assert(File.Exists(mismatch), "rejection preserves an existing non-staging file");
            Reject(() => FeishuAttachments.ValidateLocalFile(regular, 2), "local file byte limit is enforced");
            Reject(() => FeishuAttachments.ValidateLocalFile(regular, 0), "nonpositive local byte limit is rejected");
            foreach (string path in new[]
            {
                "", "SECRET.pdf", "C:SECRET.pdf", "\\SECRET.pdf", "\\\\SECRET\\share\\file.pdf",
                "//SECRET/share/file.pdf", "\\\\?\\C:\\SECRET.pdf", "\\\\.\\SECRET",
                regular + ":SECRET", regular + ":$DATA", regular + ".", regular + " ", regular + "\0SECRET",
                Path.Combine(ownedRoot, "SECRET-missing.pdf"), ownedRoot, Write([]),
                Path.Combine(ownedRoot, ".", Path.GetFileName(regular)),
                Path.Combine(ownedRoot, "..", Path.GetFileName(ownedRoot), Path.GetFileName(regular)),
                Path.Combine(ownedRoot, "CON.pdf"), Path.Combine(ownedRoot, "LPT1.txt"),
            }) Reject(() => FeishuAttachments.ValidateLocalFile(path, 100), "unsafe or invalid local path is rejected");

            // Symlink creation may require Developer Mode/privilege on Windows.
            // Only creation failures are skipped; validation assertions never are.
            void CheckLink(bool isDirectory)
            {
                string link = Path.Combine(ownedRoot, Guid.NewGuid().ToString("N"));
                try
                {
                    if (isDirectory) Directory.CreateSymbolicLink(link, ownedRoot);
                    else File.CreateSymbolicLink(link, regular);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    assert(true, "SKIP: host does not permit creating a " + (isDirectory ? "directory" : "file") + " symlink");
                    return;
                }
                links.Add((link, isDirectory));
                string linkedFile = isDirectory ? Path.Combine(link, Path.GetFileName(regular)) : link;
                Reject(() => FeishuAttachments.ValidateLocalFile(linkedFile, 100), "file and ancestor reparse points are rejected");
                if (isDirectory)
                {
                    string nested = Path.Combine(ownedRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(nested);
                    ownedDirectories.Add(nested);
                    string nestedFile = Path.Combine(nested, "inner.pdf");
                    ownedFiles.Add(nestedFile);
                    File.WriteAllBytes(nestedFile, [1]);
                    Reject(() => FeishuAttachments.ValidateLocalFile(Path.Combine(link, Path.GetFileName(nested), "inner.pdf"), 100),
                        "reparse points above the immediate parent are rejected");
                }
            }
            CheckLink(false);
            CheckLink(true);

            var attachments = new CodexInputAttachment[]
            {
                new("image", fullImage.Path, "图片.png"), new("audio", regular, "语音.opus"), unicode,
            };
            JsonElement actual = JsonSerializer.SerializeToElement(FeishuAttachments.BuildTurnInput("请检查 中文🙂", attachments));
            JsonElement expected = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "text", text = "请检查 中文🙂", text_elements = Array.Empty<object>() },
                new { type = "localImage", path = fullImage.Path },
                new { type = "localAudio", path = regular },
                new { type = "mention", name = "报告 中文🙂.pdf", path = unicode.Path },
            });
            assert(JsonElement.DeepEquals(actual, expected), "turn payload exactly matches text, localImage, localAudio and mention schemas");
            JsonElement context = JsonSerializer.SerializeToElement(FeishuAttachments.BuildContextAttachments(attachments));
            JsonElement expectedContext = JsonSerializer.SerializeToElement(attachments.Where(a => a.Kind != "image").Select(a => new { label = a.Name, path = a.Path, fsPath = a.Path }));
            assert(JsonElement.DeepEquals(context, expectedContext), "context payload has exact label/path/fsPath values including non-ASCII labels");
            foreach (IReadOnlyList<CodexInputAttachment>? empty in new IReadOnlyList<CodexInputAttachment>?[] { null, [] })
            {
                JsonElement textOnly = JsonSerializer.SerializeToElement(FeishuAttachments.BuildTurnInput("", empty));
                assert(textOnly.GetArrayLength() == 1 && textOnly[0].GetProperty("type").GetString() == "text"
                    && textOnly[0].GetProperty("text").GetString() == ""
                    && textOnly[0].GetProperty("text_elements").GetArrayLength() == 0,
                    "null/empty attachments always retain the text item, including an empty prompt");
                assert(FeishuAttachments.BuildContextAttachments(empty).Length == 0, "null/empty attachments produce empty context");
            }
            Reject(() => FeishuAttachments.BuildTurnInput("text", [new("video", regular, "clip")]), "unknown turn attachment type is rejected");
        }
        finally
        {
            // All paths originate in this invocation's generated temp directory.
            // Delete links themselves first; never recurse or follow an ancestor link.
            if ((File.GetAttributes(ownedRoot) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var link in links)
                {
                    if (link.Directory) Directory.Delete(link.Path, recursive: false);
                    else File.Delete(link.Path);
                }
                foreach (string file in ownedFiles)
                {
                    string full = Path.GetFullPath(file);
                    if (!full.StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("测试清理路径越界。");
                    bool safe = true;
                    for (DirectoryInfo? parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
                        if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) { safe = false; break; }
                    if (safe) File.Delete(full);
                }
                foreach (string child in ownedDirectories) Directory.Delete(child, recursive: false);
                Directory.Delete(ownedRoot, recursive: false);
            }
        }
        return Task.CompletedTask;
    }
}
