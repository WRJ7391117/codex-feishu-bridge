using System.Diagnostics;
using System.Security.Cryptography;

namespace DeepOriBridge.Services;

internal sealed class BridgeProductIdentity
{
    internal const string ExpectedExecutableName = "DeepOriBridge.exe";
    internal const string ExpectedProductName = "DeepOri Bridge for Windows";
    internal const string ExpectedPackageName = "E1E677B4-5C45-4603-818D-FA1592843ABC";
    internal const string ForbiddenMeshPackageName = "DeepOri.AgentMesh";
    internal const string ForbiddenMeshExecutableName = "DeepOriAgentMesh.exe";

    public string ExecutablePath { get; }
    public string SessionNonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public BridgeProductIdentity()
    {
        ExecutablePath = Environment.ProcessPath ?? string.Empty;
        string productName = ExecutablePath.Length == 0
            ? string.Empty
            : FileVersionInfo.GetVersionInfo(ExecutablePath).ProductName ?? string.Empty;
        if (!IsExpectedCandidate(ExecutablePath, productName, packageName: null))
        {
            throw new InvalidOperationException("当前进程不符合 DeepOri Bridge 产品身份契约；禁止初始化桥接服务。");
        }
    }

    internal static bool IsExpectedCandidate(
        string executablePath,
        string productName,
        string? packageName)
    {
        string fileName = Path.GetFileName(executablePath);
        if (!fileName.Equals(ExpectedExecutableName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(ForbiddenMeshExecutableName, StringComparison.OrdinalIgnoreCase)
            || !productName.Equals(ExpectedProductName, StringComparison.Ordinal))
        {
            return false;
        }

        return packageName is null
            || packageName.Equals(ExpectedPackageName, StringComparison.Ordinal);
    }

    internal static string? SelectUniqueCandidate(
        IEnumerable<(string ExecutablePath, string ProductName, string? PackageName)> candidates)
    {
        string[] matches = candidates
            .Where(candidate => IsExpectedCandidate(
                candidate.ExecutablePath,
                candidate.ProductName,
                candidate.PackageName))
            .Select(candidate => Path.GetFullPath(candidate.ExecutablePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
