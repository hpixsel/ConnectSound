using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectSound.Core.Audio;

namespace ConnectSound.Core.Platform;

public enum AppRole { Undecided, Host, Source }

public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? DeviceName { get; set; }
    public AppRole Role { get; set; }
    public SourceProfile Profile { get; set; }
    public float MasterGain { get; set; } = 1f;
    public string? LastHostId { get; set; }
    public Dictionary<string, float> SourceGains { get; set; } = [];

    [JsonIgnore] private string? _path;
    [JsonIgnore] private Timer? _debounce;

    /// <summary>For values that change continuously (faders): writes once things settle.</summary>
    public void SaveSoon()
    {
        _debounce ??= new Timer(_ => Save());
        _debounce.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    public static AppSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            settings = new AppSettings();
        }

        settings._path = path;
        return settings;
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (IOException) { /* settings are a convenience; never crash over them */ }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
