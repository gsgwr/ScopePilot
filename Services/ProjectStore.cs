using System.Text.Json;
using System.Text.Json.Serialization;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record SavedProjectInfo(Guid Id, string Name, string StartUrl, DateTimeOffset UpdatedAt)
{
    public string DisplayName => $"{Name}  |  {StartUrl}  |  {UpdatedAt:yyyy/MM/dd HH:mm}";
}

public sealed class ProjectStore
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; } = Path.Combine(ScopePilotDataPaths.RootDirectory, "projects");

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

    public async Task<EngagementProject?> LoadAsync(Guid id)
    {
        var path = Path.Combine(DataDirectory, $"{id:N}.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EngagementProject>(stream, _options);
    }

    public async Task<IReadOnlyList<SavedProjectInfo>> ListAsync()
    {
        if (!Directory.Exists(DataDirectory)) return [];
        var projects = new List<SavedProjectInfo>();
        foreach (var file in new DirectoryInfo(DataDirectory).GetFiles("*.json"))
        {
            try
            {
                await using var stream = file.OpenRead();
                using var document = await JsonDocument.ParseAsync(stream);
                var root = document.RootElement;
                var id = root.TryGetProperty("Id", out var idValue) && idValue.TryGetGuid(out var parsedId)
                    ? parsedId : Guid.ParseExact(Path.GetFileNameWithoutExtension(file.Name), "N");
                var name = root.TryGetProperty("Name", out var nameValue) ? nameValue.GetString() ?? "名称未設定" : "名称未設定";
                var startUrl = root.TryGetProperty("StartUrl", out var startValue) ? startValue.GetString() ?? string.Empty : string.Empty;
                var updatedAt = root.TryGetProperty("UpdatedAt", out var updatedValue) && updatedValue.TryGetDateTimeOffset(out var parsedUpdatedAt)
                    ? parsedUpdatedAt : file.LastWriteTime;
                projects.Add(new(id, name, startUrl, updatedAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // A broken or partially-written project must not hide other saved projects.
            }
        }
        return projects.OrderByDescending(x => x.UpdatedAt).ToArray();
    }
}
