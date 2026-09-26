using System.IO.Compression;

namespace ScopePilot.Services;

public sealed record ProjectDataSummary(int FileCount, long TotalBytes, int RunCount, int ExportCount, int ReportCount)
{
    public string Display => $"保存データ: {FileCount:N0}ファイル / {FormatBytes(TotalBytes)}　探索実行 {RunCount:N0}件　Burp出力 {ExportCount:N0}件　レポート {ReportCount:N0}件";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):N2} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes:N0} B"
    };
}

public sealed class DataManagementService
{
    private readonly string _root;

    public DataManagementService(string? root = null) => _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot");

    public string RootDirectory => _root;

    public Task<ProjectDataSummary> GetSummaryAsync(Guid projectId)
    {
        var files = ProjectFiles(projectId).Where(File.Exists).Select(path => new FileInfo(path)).ToArray();
        return Task.FromResult(new ProjectDataSummary(files.Length, files.Sum(x => x.Length),
            CountDirectories("runs", projectId), CountDirectories("exports", projectId), CountDirectories("reports", projectId)));
    }

    public async Task<string> CreateBackupAsync(Guid projectId, string projectName)
    {
        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        var safeName = string.Concat((string.IsNullOrWhiteSpace(projectName) ? "ScopePilot" : projectName)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(backupDirectory, $"{safeName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in ProjectFiles(projectId).Where(File.Exists))
        {
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            await using var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = entry.Open();
            await input.CopyToAsync(output);
        }
        return path;
    }

    public Task DeleteRunAsync(Guid projectId, string runDirectory)
    {
        var projectRuns = ProjectDirectory("runs", projectId);
        var target = Path.GetFullPath(runDirectory);
        EnsureChild(projectRuns, target);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        return Task.CompletedTask;
    }

    public Task DeleteGeneratedArtifactsAsync(Guid projectId)
    {
        foreach (var category in new[] { "exports", "reports" })
        {
            var target = ProjectDirectory(category, projectId);
            EnsureChild(Path.Combine(_root, category), target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        return Task.CompletedTask;
    }

    private IEnumerable<string> ProjectFiles(Guid projectId)
    {
        var projectFile = Path.Combine(_root, "projects", $"{projectId:N}.json");
        if (File.Exists(projectFile)) yield return projectFile;
        foreach (var category in new[] { "runs", "exports", "reports" })
        {
            var directory = ProjectDirectory(category, projectId);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) yield return file;
        }
    }

    private int CountDirectories(string category, Guid projectId)
    {
        var directory = ProjectDirectory(category, projectId);
        return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Count() : 0;
    }

    private string ProjectDirectory(string category, Guid projectId) => Path.Combine(_root, category, projectId.ToString("N"));

    private static void EnsureChild(string parent, string target)
    {
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || target.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("削除対象が案件データの保存範囲外です。");
    }
}
