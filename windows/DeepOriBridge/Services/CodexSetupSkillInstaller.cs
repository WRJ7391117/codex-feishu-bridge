namespace DeepOriBridge.Services;

internal sealed record CodexSetupSkillInstallResult(bool Success, string Message);

internal sealed class CodexSetupSkillInstaller
{
    private const string SkillName = "deepori-bridge-setup";
    private const string MarkerName = ".deepori-bridge-managed";

    public CodexSetupSkillInstallResult Install()
    {
        string source = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "CodexSkills",
            SkillName);
        string marker = Path.Combine(source, MarkerName);
        if (!Directory.Exists(source) || !File.Exists(marker))
        {
            return new(false, "App 内置的 Windows Codex 配置助手不存在。");
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

        if (Directory.Exists(destination)
            && !File.Exists(Path.Combine(destination, MarkerName)))
        {
            return new(false, "检测到非 DeepOri Bridge 管理的同名 Skill，未自动覆盖。");
        }

        string identifier = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(skillsDirectory, $".{SkillName}-install-{identifier}");
        string backup = Path.Combine(skillsDirectory, $".{SkillName}-backup-{identifier}");
        bool hadExisting = Directory.Exists(destination);
        try
        {
            Directory.CreateDirectory(skillsDirectory);
            CopyDirectory(source, staging);
            if (hadExisting)
            {
                Directory.Move(destination, backup);
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            return new(true, "Windows Codex 配置助手已安装到当前用户的 .codex\\skills。" );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            return new(false, $"安装 Windows Codex 配置助手失败：{exception.GetType().Name}");
        }
    }

    private static bool ExistsAsReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static void CopyDirectory(string source, string destination)
    {
        DirectoryInfo sourceInfo = new(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Skill 源目录不能是重解析点。");
        }

        Directory.CreateDirectory(destination);
        foreach (FileInfo file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Skill 源文件不能是重解析点。");
            }

            file.CopyTo(Path.Combine(destination, file.Name));
        }

        foreach (DirectoryInfo child in sourceInfo.EnumerateDirectories())
        {
            CopyDirectory(child.FullName, Path.Combine(destination, child.Name));
        }
    }
}
