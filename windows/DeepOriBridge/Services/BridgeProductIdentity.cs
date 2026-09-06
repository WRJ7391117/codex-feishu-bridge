using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Windows.ApplicationModel;

namespace DeepOriBridge.Services;

internal sealed record BridgeCandidateAttestation(
    string ExecutablePath,
    string ProductName,
    string PackageName,
    string PackageFamilyName,
    string Publisher,
    string PublisherId,
    string ApplicationUserModelId,
    string SignatureKind,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string ExecutableSha256,
    string PageAutomationId,
    string PackagingMode = "packaged",
    bool DeveloperPathAttested = false,
    bool PackagePathAttested = false,
    string PayloadManifestSha256 = "",
    long WindowHandle = 0);

internal sealed record BridgeWindowObservation(
    long WindowHandle,
    int ProcessId,
    bool Visible,
    string Title,
    string PageAutomationId);

internal sealed class BridgeProductIdentity
{
    internal const string ExpectedExecutableName = "DeepOriBridge.exe";
    internal const string ExpectedProductName = "DeepOri Bridge for Windows";
    internal const string ExpectedPackageName = "E1E677B4-5C45-4603-818D-FA1592843ABC";
    internal const string ExpectedPackageFamilyName = "E1E677B4-5C45-4603-818D-FA1592843ABC_1z32rh13vfry6";
    internal const string ExpectedPublisher = "CN=AppPublisher";
    internal const string ExpectedPublisherId = "1z32rh13vfry6";
    internal const string ExpectedApplicationId = "App";
    internal const string ExpectedApplicationUserModelId = ExpectedPackageFamilyName + "!" + ExpectedApplicationId;
    internal const string ForbiddenMeshPackageName = "DeepOri.AgentMesh";
    internal const string ForbiddenMeshExecutableName = "DeepOriAgentMesh.exe";
    internal const string PageAutomationIdPrefix = "DeepOriBridge.";
    internal static readonly string[] SupportedPageAutomationIds =
    [
        "DeepOriBridge.Home.Page",
        "DeepOriBridge.ConnectionSetup.Page",
        "DeepOriBridge.Configuration.Page",
        "DeepOriBridge.Diagnostics.Page",
    ];
    internal static readonly string[] SecurityCriticalPayloadFiles =
    [
        "AppxManifest.xml",
        @"Assets\CodexSkills\deepori-bridge-setup\.deepori-bridge-managed",
        @"Assets\CodexSkills\deepori-bridge-setup\SKILL.md",
        "DeepOriBridge.deps.json",
        "DeepOriBridge.dll",
        "DeepOriBridge.exe",
        "DeepOriBridge.runtimeconfig.json",
        "Microsoft.WinUI.dll",
        "resources.pri",
        @"Tools\feishu-event-sidecar.exe",
        @"Tools\feishu-event-sidecar.manifest.json",
        @"Tools\Watch-BridgeProcess.ps1",
    ];

    private readonly object _bindingLock = new();
    private BridgeCandidateAttestation? _boundInstance;

    public BridgeCandidateAttestation Current { get; private set; }
    public string ExecutablePath => Current.ExecutablePath;
    public string ExecutableSha256 => Current.ExecutableSha256;
    public int ProcessId => Current.ProcessId;
    public long ProcessStartTimeUtcTicks => Current.ProcessStartTimeUtcTicks;
    public string PayloadManifestSha256 => Current.PayloadManifestSha256;
    public bool IsDevelopmentIdentity { get; }
    public bool IsLocalPreviewRuntimeEnabled { get; }
    public bool CanUseSensitiveRuntime =>
        IsLocalPreviewRuntimeEnabled
        || !IsDevelopmentIdentity
            && !Current.SignatureKind.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !Current.SignatureKind.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    public bool IsReleaseIdentityReady =>
        !ExpectedPublisher.Equals("CN=AppPublisher", StringComparison.Ordinal)
        && Current.Publisher.Equals(ExpectedPublisher, StringComparison.Ordinal)
        && !Current.SignatureKind.Equals("None", StringComparison.OrdinalIgnoreCase);

    public BridgeProductIdentity()
    {
        BridgeCandidateAttestation current = ReadCurrentAttestation();
        IsDevelopmentIdentity = current.DeveloperPathAttested;
        BridgeCandidateAttestation? selected = SelectUniqueCandidate(
            ObserveProcessCandidates(current),
            allowDevelopmentIdentity: IsDevelopmentIdentity,
            requirePageContract: false);
        if (selected is null)
        {
            throw new InvalidOperationException(
                "当前进程未通过 DeepOri Bridge package、发布者、路径与二进制身份认证；禁止初始化桥接服务。");
        }
        Current = selected;
        IsLocalPreviewRuntimeEnabled = IsDevelopmentIdentity
            && Current.PackagingMode.Equals("packaged", StringComparison.Ordinal)
            && Current.PackagePathAttested
            && HasAssemblyMetadataFlag("DeepOriBridge.LocalPreviewSensitiveRuntime");
    }

    private static bool HasAssemblyMetadataFlag(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => attribute.Key.Equals(key, StringComparison.Ordinal)
                && string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static bool IsExplicitDeveloperBuild(
        string executablePath,
        string canonicalRootMetadataKey)
    {
        Dictionary<string, string> metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Value is not null)
            .ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value!,
                StringComparer.Ordinal);
        if (!metadata.TryGetValue("DeepOriBridge.DeveloperBuild", out string? enabled)
            || !enabled.Equals("true", StringComparison.OrdinalIgnoreCase)
            || !metadata.TryGetValue(
                canonicalRootMetadataKey,
                out string? expectedRoot))
        {
            return false;
        }

        string canonicalRoot = Path.GetFullPath(expectedRoot);
        string expectedExecutable = Path.Combine(canonicalRoot, ExpectedExecutableName);
        return Path.GetFullPath(executablePath).Equals(
            expectedExecutable,
            StringComparison.OrdinalIgnoreCase);
    }

    internal BridgeCandidateAttestation BindPageContract(string pageAutomationId)
    {
        if (!SupportedPageAutomationIds.Contains(pageAutomationId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Bridge 页面 AutomationId 不符合产品身份契约。");
        }

        BridgeWindowObservation? observed = SelectUniqueWindow(
            ObserveTopLevelWindows(Current.ProcessId),
            Current.ProcessId,
            pageAutomationId);
        if (observed is null)
        {
            throw new InvalidOperationException(
                "没有观测到唯一且属于当前 Bridge 进程的精确 HWND/UIA 页面契约。" );
        }

        lock (_bindingLock)
        {
            _boundInstance = Current with
            {
                PageAutomationId = observed.PageAutomationId,
                WindowHandle = observed.WindowHandle,
            };
            return _boundInstance;
        }
    }

    internal BridgeCandidateAttestation GetBoundInstance()
    {
        BridgeCandidateAttestation bound;
        lock (_bindingLock)
        {
            if (_boundInstance is null)
            {
                throw new InvalidOperationException("Bridge 窗口尚未绑定已验证页面契约。");
            }
            bound = _boundInstance;
        }

        BridgeCandidateAttestation? process = SelectUniqueCandidate(
            ObserveProcessCandidates(Current),
            allowDevelopmentIdentity: IsDevelopmentIdentity,
            requirePageContract: false);
        BridgeWindowObservation? window = SelectUniqueWindow(
            ObserveTopLevelWindows(Current.ProcessId),
            Current.ProcessId,
            bound.PageAutomationId);
        if (process is null
            || process.ProcessId != bound.ProcessId
            || process.ProcessStartTimeUtcTicks != bound.ProcessStartTimeUtcTicks
            || !FileAttestation.FixedTimeEquals(
                process.ExecutableSha256,
                bound.ExecutableSha256)
            || !FileAttestation.FixedTimeEquals(
                process.PayloadManifestSha256,
                bound.PayloadManifestSha256)
            || window is null
            || window.WindowHandle != bound.WindowHandle)
        {
            throw new InvalidOperationException(
                "Bridge 进程、payload 或 HWND/UIA 观测已变化；身份绑定失效。" );
        }

        return process with
        {
            PageAutomationId = window.PageAutomationId,
            WindowHandle = window.WindowHandle,
        };
    }

    internal static bool IsExpectedCandidate(
        BridgeCandidateAttestation candidate,
        bool allowDevelopmentIdentity,
        bool requirePageContract = true)
    {
        string fileName = Path.GetFileName(candidate.ExecutablePath);
        bool exactProcess = fileName.Equals(ExpectedExecutableName, StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals(ForbiddenMeshExecutableName, StringComparison.OrdinalIgnoreCase)
            && candidate.ProductName.Equals(ExpectedProductName, StringComparison.Ordinal)
            && candidate.ProcessId > 0
            && candidate.ProcessStartTimeUtcTicks > 0
            && candidate.ExecutableSha256.Length == 64
            && candidate.PayloadManifestSha256.Length == 64
            && (!requirePageContract
                || SupportedPageAutomationIds.Contains(
                    candidate.PageAutomationId,
                    StringComparer.Ordinal)
                    && candidate.WindowHandle != 0);
        if (!exactProcess)
        {
            return false;
        }

        if (candidate.PackagingMode.Equals("unpackaged-dev", StringComparison.Ordinal))
        {
            return allowDevelopmentIdentity
                && candidate.DeveloperPathAttested
                && candidate.PackageName.Length == 0
                && candidate.PackageFamilyName.Length == 0
                && candidate.Publisher.Length == 0
                && candidate.PublisherId.Length == 0
                && candidate.ApplicationUserModelId.Length == 0
                && candidate.SignatureKind.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        bool exactPackage = candidate.PackagingMode.Equals("packaged", StringComparison.Ordinal)
            && candidate.PackageName.Equals(ExpectedPackageName, StringComparison.Ordinal)
            && candidate.PackageFamilyName.Equals(ExpectedPackageFamilyName, StringComparison.Ordinal)
            && candidate.Publisher.Equals(ExpectedPublisher, StringComparison.Ordinal)
            && candidate.PublisherId.Equals(ExpectedPublisherId, StringComparison.Ordinal)
            && candidate.ApplicationUserModelId.Equals(ExpectedApplicationUserModelId, StringComparison.Ordinal)
            && candidate.PackagePathAttested;
        bool signed = !candidate.SignatureKind.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !candidate.SignatureKind.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        return exactPackage
            && (signed
                || allowDevelopmentIdentity && candidate.DeveloperPathAttested);
    }

    internal static BridgeCandidateAttestation? SelectUniqueCandidate(
        IEnumerable<BridgeCandidateAttestation> candidates,
        bool allowDevelopmentIdentity,
        bool requirePageContract = true)
    {
        BridgeCandidateAttestation[] matches = candidates
            .Where(candidate => IsExpectedCandidate(
                candidate,
                allowDevelopmentIdentity,
                requirePageContract))
            .GroupBy(
                candidate => $"{Path.GetFullPath(candidate.ExecutablePath)}|{candidate.ProcessId}|{candidate.ProcessStartTimeUtcTicks}|{candidate.ExecutableSha256}|{candidate.PayloadManifestSha256}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static BridgeWindowObservation? SelectUniqueWindow(
        IEnumerable<BridgeWindowObservation> observations,
        int expectedProcessId,
        string expectedPageAutomationId)
    {
        BridgeWindowObservation[] matches = observations.Where(observation =>
            observation.WindowHandle != 0
            && observation.ProcessId == expectedProcessId
            && observation.Visible
            && observation.Title.Equals(ExpectedProductName, StringComparison.Ordinal)
            && SupportedPageAutomationIds.Contains(
                observation.PageAutomationId,
                StringComparer.Ordinal)
            && observation.PageAutomationId.Equals(
                expectedPageAutomationId,
                StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<BridgeCandidateAttestation> ObserveProcessCandidates(
        BridgeCandidateAttestation current)
    {
        var observed = new List<BridgeCandidateAttestation>();
        foreach (Process process in Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(ExpectedExecutableName)))
        {
            using (process)
            {
                try
                {
                    string path = FileAttestation.CanonicalPath(
                        process.MainModule?.FileName ?? string.Empty);
                    bool sameCanonicalPath = path.Equals(
                        current.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                    observed.Add(current with
                    {
                        ExecutablePath = path,
                        ProductName = FileVersionInfo.GetVersionInfo(path).ProductName ?? string.Empty,
                        ProcessId = process.Id,
                        ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        ExecutableSha256 = FileAttestation.Sha256(path),
                        PayloadManifestSha256 = sameCanonicalPath
                            ? FileAttestation.PayloadManifestSha256(
                                Path.GetDirectoryName(path)!,
                                SecurityCriticalPayloadFiles)
                            : string.Empty,
                        DeveloperPathAttested = sameCanonicalPath && current.DeveloperPathAttested,
                        PackagePathAttested = sameCanonicalPath && current.PackagePathAttested,
                    });
                }
                catch
                {
                }
            }
        }
        return observed;
    }

    private static IEnumerable<BridgeWindowObservation> ObserveTopLevelWindows(int processId)
    {
        var windows = new List<BridgeWindowObservation>();
        EnumWindows((window, state) =>
        {
            _ = GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(window))
            {
                return true;
            }

            var title = new StringBuilder(512);
            _ = GetWindowText(window, title, title.Capacity);
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AutomationElementCollection descendants = root.FindAll(
                    TreeScope.Descendants,
                    Condition.TrueCondition);
                string[] pages = Enumerable.Range(0, descendants.Count)
                    .Select(index => descendants[index].Current.AutomationId)
                    .Where(id => SupportedPageAutomationIds.Contains(id, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (pages.Length == 1)
                {
                    windows.Add(new BridgeWindowObservation(
                        window.ToInt64(),
                        processId,
                        true,
                        title.ToString(),
                        pages[0]));
                }
            }
            catch
            {
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static BridgeCandidateAttestation ReadCurrentAttestation()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法取得当前 Bridge 可执行文件路径。");
        string canonicalPath = FileAttestation.CanonicalPath(processPath);
        if (FileAttestation.HasReparsePoint(canonicalPath))
        {
            throw new InvalidOperationException("Bridge 可执行文件路径包含重解析点。");
        }

        Package? package = null;
        try
        {
            package = Package.Current;
        }
        catch (InvalidOperationException)
        {
            if (!IsExplicitDeveloperBuild(
                    canonicalPath,
                    "DeepOriBridge.DeveloperCanonicalLooseRoot"))
            {
                throw new InvalidOperationException(
                    "当前 Bridge 进程没有 package identity，且不在构建时绑定的显式 loose dev 输出中；禁止启动。");
            }
        }

        if (package is null)
        {
            using Process looseProcess = Process.GetCurrentProcess();
            return new BridgeCandidateAttestation(
                canonicalPath,
                FileVersionInfo.GetVersionInfo(canonicalPath).ProductName ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "None",
                looseProcess.Id,
                looseProcess.StartTime.ToUniversalTime().Ticks,
                FileAttestation.Sha256(canonicalPath),
                string.Empty,
                "unpackaged-dev",
                DeveloperPathAttested: true,
                PayloadManifestSha256: FileAttestation.PayloadManifestSha256(
                    Path.GetDirectoryName(canonicalPath)!,
                    SecurityCriticalPayloadFiles));
        }

        string installedPath = Path.GetFullPath(package.InstalledLocation.Path);
        string expectedExecutable = Path.GetFullPath(Path.Combine(installedPath, ExpectedExecutableName));
        if (!canonicalPath.Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Bridge 可执行文件不在当前精确 package 的规范安装路径中。");
        }

        bool developerPathAttested = IsExplicitDeveloperBuild(
            canonicalPath,
            "DeepOriBridge.DeveloperCanonicalAppxRoot");

        using Process process = Process.GetCurrentProcess();
        return new BridgeCandidateAttestation(
            canonicalPath,
            FileVersionInfo.GetVersionInfo(canonicalPath).ProductName ?? string.Empty,
            package.Id.Name,
            package.Id.FamilyName,
            package.Id.Publisher,
            package.Id.PublisherId,
            GetCurrentApplicationUserModelIdValue(),
            package.SignatureKind.ToString(),
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            FileAttestation.Sha256(canonicalPath),
            string.Empty,
            "packaged",
            developerPathAttested,
            PackagePathAttested: true,
            PayloadManifestSha256: FileAttestation.PayloadManifestSha256(
                installedPath,
                SecurityCriticalPayloadFiles));
    }

    private static string GetCurrentApplicationUserModelIdValue()
    {
        uint length = 0;
        int first = GetCurrentApplicationUserModelId(ref length, null);
        const int ErrorInsufficientBuffer = 122;
        if (first != ErrorInsufficientBuffer || length == 0)
        {
            throw new InvalidOperationException("无法取得当前 package 的 ApplicationUserModelId。");
        }

        var value = new StringBuilder((int)length);
        int second = GetCurrentApplicationUserModelId(ref length, value);
        if (second != 0)
        {
            throw new InvalidOperationException("无法核验当前 package 的 ApplicationUserModelId。");
        }

        return value.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
