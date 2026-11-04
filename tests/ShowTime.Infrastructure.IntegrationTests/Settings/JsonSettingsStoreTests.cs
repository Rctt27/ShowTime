using System.IO.Abstractions;
using System.Text.Json;
using ShowTime.Core.Geometry;
using ShowTime.Core.Settings;
using ShowTime.Infrastructure.Settings;

namespace ShowTime.Infrastructure.IntegrationTests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy");

    private readonly TempFolder _folder = new();
    private readonly SettingsDefaults _defaults = SettingsDefaults.ForWindows(@"C:\Default");
    private readonly string _file;
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests()
    {
        _file = Path.Combine(_folder.Path, "ShowTime", "settings.json");
        _store = new JsonSettingsStore(new FileSystem(), _file, _defaults);
    }

    public void Dispose() => _folder.Dispose();

    private void Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, json);
    }

    [Fact]
    public void A_missing_file_gives_the_platform_defaults()
    {
        var settings = _store.Load();
        settings.OutputFolder.ShouldBe(@"C:\Default");
        settings.HotkeyRegion.ShouldBe("PrintScreen");
        settings.Counter.ShouldBe(1);
    }

    [Fact]
    public void A_v0_1_0_file_loads_every_value()
    {
        Write(File.ReadAllText(Path.Combine(Fixtures, "settings-v0.1.0.json")));

        var s = _store.Load();

        s.OutputFolder.ShouldBe(@"D:\Captures");
        s.FileNamePattern.ShouldBe("Shot_%NUM%");
        s.Format.ShouldBe("jpg");
        s.JpegQuality.ShouldBe(75);
        s.AfterCapture.ShouldBe(AfterCaptureAction.SaveAndCopy);
        s.CopyPathAfterSave.ShouldBeTrue();
        s.HotkeyRegion.ShouldBe("Ctrl+Shift+A");
        s.HotkeyWindow.ShouldBe("");
        s.HotkeyFullScreen.ShouldBe("F9");
        s.HotkeyLastRegion.ShouldBe("Ctrl+Alt+L");
        s.HotkeyVideoRegion.ShouldBe("Ctrl+Shift+V");
        s.HotkeyVideoFullScreen.ShouldBe("Ctrl+Shift+F");
        s.HotkeyVideoStart.ShouldBe("Ctrl+Alt+R");
        s.HotkeyVideoStop.ShouldBe("Ctrl+Alt+S");
        s.FfmpegPath.ShouldBe(@"C:\Tools\ffmpeg.exe");
        s.Microphone.ShouldBe("Mic B");
        s.RecordMicrophone.ShouldBeFalse();
        s.RecordSystemAudio.ShouldBeTrue();
        s.VideoFrameRate.ShouldBe(24);
        s.LastRegion.ShouldBe(new PixelRect(-100, 50, 800, 600));
        s.Counter.ShouldBe(17);
        s.LastSaveAsFolder.ShouldBe(@"E:\Elsewhere");
        s.EditorColor.ShouldBe("#1E88E5");
        s.EditorThickness.ShouldBe(6);
        s.EditorFontSize.ShouldBe(32);
        s.SchemaVersion.ShouldBe(1);
        // Added after v0.1.0: an older file gets the defaults, transcription off.
        s.TranscribeMicrophone.ShouldBeFalse();
        s.TranscriptionLanguage.ShouldBe("fr");
        s.TranscriptionModelPath.ShouldBe("");
        s.HighlightTrigger.ShouldBe("Ctrl+Space");
    }

    [Fact]
    public void A_saved_file_has_the_keys_of_v0_1_0_plus_the_schema_version_the_transcription_and_the_highlight()
    {
        _store.Save(_store.Load());

        var written = KeysOf(File.ReadAllText(_file));
        var legacy = KeysOf(File.ReadAllText(Path.Combine(Fixtures, "settings-v0.1.0-defaults.json")));
        // Deliberate change: the transcription's three settings are new keys, which v0.1.0 files simply lack.
        // Deliberate change: the highlight trigger is a new key, which older files simply lack.
        // Deliberate change: the highlight style is a new key, which older files simply lack.
        written.ShouldBe([.. legacy, "SchemaVersion", "TranscribeMicrophone", "TranscriptionLanguage", "TranscriptionModelPath", "HighlightTrigger",
            "HighlightStyle"], ignoreOrder: true);
    }

    [Fact]
    public void The_highlight_style_survives_a_round_trip()
    {
        var settings = _store.Load();
        settings.HighlightStyle = HighlightStyle.Default with { Thickness = 12.5, Opacity = 0.6 };
        _store.Save(settings);

        _store.Load().HighlightStyle.ShouldBe(HighlightStyle.Default with { Thickness = 12.5, Opacity = 0.6 });
    }

    [Fact]
    public void A_file_without_the_highlight_style_or_with_part_of_it_keeps_the_defaults()
    {
        Write("""{ "Counter": 5 }""");
        _store.Load().HighlightStyle.ShouldBe(HighlightStyle.Default);

        Write("""{ "HighlightStyle": { "Thickness": 12 } }""");
        _store.Load().HighlightStyle.ShouldBe(HighlightStyle.Default with { Thickness = 12 });
    }

    [Fact]
    public void Enums_are_written_by_name_as_in_v0_1_0()
    {
        var settings = _store.Load();
        settings.AfterCapture = AfterCaptureAction.CopyToClipboard;
        _store.Save(settings);
        File.ReadAllText(_file).ShouldContain("\"AfterCapture\": \"CopyToClipboard\"");
    }

    [Fact]
    public void Values_survive_a_round_trip()
    {
        var settings = _store.Load();
        settings.LastRegion = new PixelRect(1, 2, 3, 4);
        settings.Counter = 99;
        _store.Save(settings);

        var loaded = _store.Load();
        loaded.LastRegion.ShouldBe(new PixelRect(1, 2, 3, 4));
        loaded.Counter.ShouldBe(99);
    }

    [Fact]
    public void Keys_missing_from_the_file_keep_their_default()
    {
        Write("""{ "Counter": 5 }""");
        var settings = _store.Load();
        settings.Counter.ShouldBe(5);
        settings.HotkeyVideoStop.ShouldBe("Ctrl+D");
        settings.OutputFolder.ShouldBe(@"C:\Default");
    }

    [Fact]
    public void A_blank_output_folder_falls_back_to_the_default()
    {
        Write("""{ "OutputFolder": "  " }""");
        _store.Load().OutputFolder.ShouldBe(@"C:\Default");
    }

    [Fact]
    public void A_corrupt_file_does_not_prevent_starting()
    {
        Write("{ not json");
        _store.Load().Counter.ShouldBe(1);
    }

    [Fact]
    public void A_newer_file_with_unknown_keys_still_loads()
    {
        Write("""{ "SchemaVersion": 7, "Counter": 3, "SomethingNew": true }""");
        _store.Load().Counter.ShouldBe(3);
    }

    [Fact]
    public void TrySave_reports_a_locked_file_instead_of_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "File locks are advisory on Unix.");
        _store.Save(_store.Load());
        using var locked = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);
        _store.TrySave(_store.Load()).ShouldBeFalse();
    }

    private static List<string> KeysOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }
}
