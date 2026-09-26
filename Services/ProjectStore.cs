using System.Text.Json;
using System.Text.Json.Serialization;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class ProjectStore
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot", "projects");

    public async Task SaveAsync(EngagementProject project)
    {
        Directory.CreateDirectory(DataDirectory);
        project.UpdatedAt = DateTimeOffset.Now;
        var path = Path.Combine(DataDirectory, $"{project.Id:N}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, project, _options);
    }

    public async Task<EngagementProject?> LoadLatestAsync()
    {
        if (!Directory.Exists(DataDirectory)) return null;
        var file = new DirectoryInfo(DataDirectory).GetFiles("*.json")
            .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        if (file is null) return null;
        await using var stream = file.OpenRead();
        return await JsonSerializer.DeserializeAsync<EngagementProject>(stream, _options);
    }
}
