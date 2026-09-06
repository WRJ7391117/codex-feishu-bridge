using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Storage;

namespace DeepOriBridge.Services;

// A process-scoped companion, not a scheduled task or an auto-start installation.
// Normal user close/update explicitly disarms recovery before shutting services down.
internal static class BridgeCrashRecovery
{
    private static string? _normalExitMarker;
    internal static void Start()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            string state = Path.Combine(BridgeLocalStorage.Root, "CrashRecovery");
            Directory.CreateDirectory(state);
            string identity = current.Id + "-" + current.StartTime.ToUniversalTime().Ticks;
            _normalExitMarker = Path.Combine(state, identity + ".normal");
            string script = Path.Combine(AppContext.BaseDirectory, "Tools", "Watch-BridgeProcess.ps1");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-File", script,
                "-BridgeProcessId", current.Id.ToString(), "-StartTicks", current.StartTime.ToUniversalTime().Ticks.ToString(),
                "-StateDirectory", state }) start.ArgumentList.Add(value);
            if (PortablePreview.Enabled)
            {
                start.ArgumentList.Add("-Portable");
                start.ArgumentList.Add("-ExpectedExecutableHash");
                start.ArgumentList.Add(FileAttestation.Sha256(Environment.ProcessPath!));
            }
            else
            {
                start.ArgumentList.Add("-PackageFamily");
                start.ArgumentList.Add(Package.Current.Id.FamilyName);
            }
            using Process? helper = Process.Start(start);
        }
        catch (Exception exception)
        {
            // Failure to supervise must not prevent the user's bridge from starting.
            Debug.WriteLine("Bridge crash recovery unavailable: " + exception.GetType().Name);
        }
    }
    internal static void Disarm()
    {
        try { if (_normalExitMarker is { } path) File.WriteAllText(path, "normal-close"); }
        catch (Exception exception) { Debug.WriteLine("Recovery disarm failed: " + exception.GetType().Name); }
    }
}
