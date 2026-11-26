namespace DeepOriBridge.Services;

internal sealed record CodexSetupSkillInstallResult(bool Success, string Message);

internal sealed class CodexSetupSkillInstaller
{
    private const string SkillName = "deepori-bridge-setup";
    private const string MarkerName = ".deepori-bridge-managed";

    public CodexSetupSkillInstallResult Install()
    {
        string source = SourceDirectory();
        if (!ValidateManagedSkill(source))
        {
            return new(false, "App 内置 Skill 的 marker/hash 无效；没有安装。");
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string codexDirectory = Path.Combine(profile, ".codex");
        string skillsDirectory = Path.Combine(codexDirectory, "skills");
        string destination = Path.Combine(skillsDirectory, SkillName);
        foreach (string path in new[] { codexDirectory, skillsDirectory, destination })
        {
            if (ExistsAsReparsePoint(path))
            {
                return new(false, "Codex Skill 路径包含重解析点；已停止安装。");
            }
        }

        if (Directory.Exists(destination) && !ValidateManagedSkill(destination))
        {
            return new(false, "同名 Skill 不是 Bridge 管理的精确版本；不会覆盖。");
        }

        Directory.CreateDirectory(codexDirectory);
        Directory.CreateDirectory(skillsDirectory);
        if (ExistsAsReparsePoint(codexDirectory) || ExistsAsReparsePoint(skillsDirectory))
        {
            return new(false, "Codex Skill 父目录在创建后解析为重解析点；已停止安装。");
        }

        string transaction = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(skillsDirectory, $".{SkillName}.stage.{transaction}");
        string backup = Path.Combine(skillsDirectory, $".{SkillName}.backup.{transaction}");
        bool oldMoved = false;
        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(Path.Combine(source, "SKILL.md"), Path.Combine(staging, "SKILL.md"));
            File.Copy(Path.Combine(source, MarkerName), Path.Combine(staging, MarkerName));
            if (!ValidateManagedSkill(staging))
            {
                throw new InvalidOperationException("staging Skill 完整性验证失败。");
            }

            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                oldMoved = true;
            }
            Directory.Move(staging, destination);
            if (!ValidateManagedSkill(destination))
            {
                throw new InvalidOperationException("安装后的 Skill 完整性验证失败。");
            }
            DeleteTransactionDirectory(backup, skillsDirectory);
            return new(true, "Codex 配置 Skill 已按 marker/hash 原子安装到当前 Windows 用户。");
        }
        catch (Exception exception)
        {
            DeleteTransactionDirectory(staging, skillsDirectory);
            if (oldMoved && Directory.Exists(backup))
            {
                DeleteManagedDestination(destination, skillsDirectory);
                Directory.Move(backup, destination);
            }
            return new(false, $"Codex Skill 安装失败：{exception.GetType().Name}；旧版本已保留。");
        }
    }

    public CodexSetupSkillInstallResult VerifyInstalled()
    {
        string source = SourceDirectory();
        string marker = Path.Combine(source, MarkerName);
        if (!Directory.Exists(source)
            || !File.Exists(marker)
            || !ValidateManagedSkill(source))
        {
            return new(false, "App 内置 Skill 不存在，或其 marker/hash 不一致。");
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string codexDirectory = Path.Combine(profile, ".codex");
        string skillsDirectory = Path.Combine(codexDirectory, "skills");
        string destination = Path.Combine(skillsDirectory, SkillName);
        foreach (string path in new[] { codexDirectory, skillsDirectory, destination })
        {
            if (ExistsAsReparsePoint(path))
            {
                return new(false, "Codex Skill 路径包含重解析点；已停止自动安装。");
            }
        }

        if (!Directory.Exists(destination))
        {
            return new(false, "当前用户尚未安装 DeepOri Bridge Skill；自动安装保持禁用。");
        }

        if (!ValidateManagedSkill(destination))
        {
            return new(false, "已安装 Skill 的内容或 marker/hash 不一致；禁止自动覆盖。");
        }

        string sourceHash = FileAttestation.Sha256(Path.Combine(source, "SKILL.md"));
        string installedHash = FileAttestation.Sha256(Path.Combine(destination, "SKILL.md"));
        return FileAttestation.FixedTimeEquals(sourceHash, installedHash)
            ? new(true, "内置与已安装 Skill 的内容、hash 和 marker 完全一致。")
            : new(false, "内置与已安装 Skill 不一致；自动配置保持禁用。");
    }

    internal static bool ValidateManagedSkill(string directory)
    {
        string marker = Path.Combine(directory, MarkerName);
        string skill = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(marker)
            || !File.Exists(skill)
            || ExistsAsReparsePoint(directory)
            || ExistsAsReparsePoint(marker)
            || ExistsAsReparsePoint(skill))
        {
            return false;
        }

        const string Prefix = "SKILL_SHA256=";
        string? markerHash = File.ReadLines(marker)
            .FirstOrDefault(line => line.StartsWith(Prefix, StringComparison.Ordinal))?
            [Prefix.Length..]
            .Trim();
        return markerHash?.Length == 64
            && FileAttestation.FixedTimeEquals(
                markerHash,
                FileAttestation.Sha256(skill));
    }

    private static bool ExistsAsReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string SourceDirectory() => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "CodexSkills",
        SkillName);

    private static void DeleteTransactionDirectory(string path, string skillsDirectory)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        string canonicalParent = Path.GetFullPath(skillsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string canonicalPath = Path.GetFullPath(path);
        string name = Path.GetFileName(canonicalPath);
        if (!canonicalPath.StartsWith(canonicalParent, StringComparison.OrdinalIgnoreCase)
            || (!name.StartsWith($".{SkillName}.stage.", StringComparison.Ordinal)
                && !name.StartsWith($".{SkillName}.backup.", StringComparison.Ordinal))
            || ExistsAsReparsePoint(canonicalPath))
        {
            throw new InvalidOperationException("拒绝清理不属于 Bridge 安装事务的目录。");
        }
        Directory.Delete(canonicalPath, recursive: true);
    }

    private static void DeleteManagedDestination(string path, string skillsDirectory)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        string expected = Path.GetFullPath(Path.Combine(skillsDirectory, SkillName));
        string canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !ValidateManagedSkill(canonicalPath))
        {
            throw new InvalidOperationException("拒绝清理非 Bridge 管理的 Skill 目录。");
        }
        Directory.Delete(canonicalPath, recursive: true);
    }

}
