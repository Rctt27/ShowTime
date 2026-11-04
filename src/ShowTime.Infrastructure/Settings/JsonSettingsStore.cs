using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ShowTime.Core.Settings;

namespace ShowTime.Infrastructure.Settings;

/// <summary>
/// settings.json, as v0.1.0 wrote it: indented, enums by name, one key per property. Read and written through
/// metadata generated at compile time (<see cref="SettingsJsonContext"/>): reflection would break in the trimmed macOS app.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _filePath;
    private readonly SettingsDefaults _defaults;
    private readonly JsonTypeInfo<AppSettings> _typeInfo;

    public JsonSettingsStore(IFileSystem fileSystem, string filePath, SettingsDefaults defaults)
    {
        _fileSystem = fileSystem;
        _filePath = filePath;
        _defaults = defaults;
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            // A key missing from the file keeps this platform's default rather than a C# default.
            TypeInfoResolver = SettingsJsonContext.Default.WithAddedModifier(info =>
            {
                if (info.Type == typeof(AppSettings))
                    info.CreateObject = () => new AppSettings(_defaults);
            })
        };
        _typeInfo = (JsonTypeInfo<AppSettings>)options.GetTypeInfo(typeof(AppSettings));
    }

    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (_fileSystem.File.Exists(_filePath))
                settings = JsonSerializer.Deserialize(_fileSystem.File.ReadAllText(_filePath), _typeInfo);
        }
#pragma warning disable CA1031 // A corrupt file must not prevent the app from starting; defaults take over and overwrite it on next save.
        catch (Exception)
#pragma warning restore CA1031
        {
            settings = null;
        }

        settings ??= new AppSettings(_defaults);
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
            settings.OutputFolder = _defaults.OutputFolder;
        // The JSON metadata ignores nullable annotations: "HighlightStyle": null, written by hand, arrives as null.
        settings.HighlightStyle ??= HighlightStyle.Default;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(_filePath)!);
        _fileSystem.File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, _typeInfo));
    }

    public bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The metadata of settings.json, generated at compile time. Metadata only, without the generated fast writer, so the
/// file goes through the same converters as before, enums by name included.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
