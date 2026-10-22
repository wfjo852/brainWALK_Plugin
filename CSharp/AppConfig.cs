using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrainWalk;

internal sealed class AppConfig
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "lidar_config.json");
    private JsonObject _root;

    public AppConfig()
    {
        _root = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path))?.AsObject() ?? new() : new();
    }

    // Long-lived AppConfig instances (e.g. the launcher's) can go stale while another
    // window (e.g. the settings form) saves its own changes to the same file in the
    // meantime. Reload right before writing so a save never clobbers those changes.
    public void Reload()
    {
        _root = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path))?.AsObject() ?? new() : new();
    }

    public T Get<T>(string section, string key, T fallback)
    {
        try
        {
            var node = _root[section]?[key];
            return node is null ? fallback : node.Deserialize<T>() ?? fallback;
        }
        catch { return fallback; }
    }

    public void Set<T>(string section, string key, T value)
    {
        if (_root[section] is not JsonObject) _root[section] = new JsonObject();
        _root[section]![key] = JsonSerializer.SerializeToNode(value);
    }

    public void Save() => File.WriteAllText(_path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
