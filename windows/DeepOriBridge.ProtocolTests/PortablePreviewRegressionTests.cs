using System.Security.Cryptography;
using System.Text.Json;
using DeepOriBridge.Services;

internal static class PortablePreviewRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        string root = Path.Combine(Path.GetTempPath(), "bridge-portable-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string payload = Path.Combine(root, "中文 payload");
            Directory.CreateDirectory(Path.Combine(payload, "Tools"));
            foreach (string path in new[] { "DeepOriBridge.exe", "DeepOriBridge.dll", "Tools/feishu-event-sidecar.exe" })
                File.WriteAllText(Path.Combine(payload, path), "fixture-" + path);
            var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(payload, f).Replace('\\', '/'), FileAttestation.Sha256);
            string manifest = Path.Combine(payload, PortablePreview.ManifestName);
            File.WriteAllText(manifest, JsonSerializer.Serialize(new PortablePreview.Payload(
                1, PortablePreview.Channel, new string('a', 40), files)));
            assert(PortablePreview.VerifyPayload(payload).Length == 4, "portable complete payload accepts Unicode/space paths");
            string hash = FileAttestation.PayloadManifestSha256(payload, PortablePreview.VerifyPayload(payload));
            var consent = new PortablePreview.Consent(1, Path.GetFullPath(payload), hash);
            assert(PortablePreview.MatchesConsent(consent, payload, hash), "portable exact consent matches");
            assert(!PortablePreview.MatchesConsent(null, payload, hash), "portable first run requires consent");
            assert(!PortablePreview.MatchesConsent(consent, root, hash), "portable relocation requires consent");
            assert(!PortablePreview.MatchesConsent(consent, payload, new string('F', 64)), "portable update requires consent");
            assert(!PortablePreview.MatchesConsent(consent with { Version = 2 }, payload, hash), "unknown consent version rejected");
            File.WriteAllText(Path.Combine(payload, "extra.dll"), "unexpected");
            Reject(() => PortablePreview.VerifyPayload(payload), "extra executable payload rejected");
            File.Delete(Path.Combine(payload, "extra.dll"));
            File.WriteAllText(Path.Combine(payload, "DeepOriBridge.dll"), "changed");
            Reject(() => PortablePreview.VerifyPayload(payload), "tampered DLL rejected");
            File.Delete(Path.Combine(payload, "DeepOriBridge.dll"));
            Reject(() => PortablePreview.VerifyPayload(payload), "missing DLL rejected");
            string settingsA = Path.Combine(root, "user-a");
            string settingsB = Path.Combine(root, "user-b");
            assert(PortablePreview.ReadSettingAt(settingsA, "selection") is null, "new portable storage is empty");
            PortablePreview.WriteSettingAt(settingsA, "selection", "private-value");
            assert(PortablePreview.ReadSettingAt(settingsA, "selection") == "private-value", "portable encrypted settings round trip");
            assert(PortablePreview.ReadSettingAt(settingsB, "selection") is null, "portable storage scopes are separate");
            string stored = Directory.GetFiles(Path.Combine(settingsA, "Settings"), "*.bin").Single();
            assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(stored)).Contains("private-value"), "portable settings not plaintext");
            using (var held = new FileStream(stored + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => PortablePreview.WriteSettingAt(settingsA, "selection", "lost-update"), "competing settings writer rejected");
            assert(PortablePreview.ReadSettingAt(settingsA, "selection") == "private-value", "busy settings write preserves previous value");
            PortablePreview.WriteSettingAt(settingsA, "selection", "updated");
            assert(PortablePreview.ReadSettingAt(settingsA, "selection") == "updated", "portable settings replacement succeeds");
            File.WriteAllText(stored, "corrupt");
            Reject(() => PortablePreview.ReadSettingAt(settingsA, "selection"), "corrupt encrypted settings rejected");
            var candidate = new BridgeCandidateAttestation(Path.Combine(payload, "DeepOriBridge.exe"),
                BridgeProductIdentity.ExpectedProductName, "", "", "", "", "", "None", 123, 123,
                hash, "", PackagingMode: PortablePreview.Channel, PayloadManifestSha256: hash, PortableUserTrusted: true);
            assert(!BridgeProductIdentity.IsExpectedCandidate(candidate, false, false), "portable identity not implicitly allowed in packaged policy");
            assert(BridgeProductIdentity.IsExpectedCandidate(candidate, false, false, true), "explicit portable policy accepts trusted unsigned identity");
            assert(!BridgeProductIdentity.IsExpectedCandidate(candidate with { PortableUserTrusted = false }, false, false, true), "untrusted portable identity rejected");
            assert(!BridgeProductIdentity.IsExpectedCandidate(candidate with { Publisher = "forged" }, false, false, true), "portable cannot claim publisher identity");
            assert(!PortablePreview.IsTrusted(payload, hash), "test assembly cannot enable portable sensitive runtime");
        }
        finally { Directory.Delete(root, recursive: true); }
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is IOException or CryptographicException) { rejected = true; }
            assert(rejected, description);
        }
    }
}
