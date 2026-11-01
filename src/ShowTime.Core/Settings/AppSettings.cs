using System.Text.Json.Serialization;
using ShowTime.Core.Geometry;

namespace ShowTime.Core.Settings;

public enum AfterCaptureAction
{
    OpenEditor,
    SaveToFile,
    CopyToClipboard,
    SaveAndCopy
}

/// <summary>
/// Everything stored in settings.json. Property names are the file's keys: renaming one breaks older files, which
/// must keep loading (see CLAUDE.md). A key missing from the file keeps the value given by <see cref="SettingsDefaults"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Version of the file layout. v0.1.0 files have none, which reads as 1.</summary>
    public const int CurrentSchemaVersion = 1;

    public AppSettings(SettingsDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        OutputFolder = defaults.OutputFolder;
        HotkeyRegion = defaults.HotkeyRegion;
        HotkeyWindow = defaults.HotkeyWindow;
        HotkeyFullScreen = defaults.HotkeyFullScreen;
        HotkeyLastRegion = defaults.HotkeyLastRegion;
        HotkeyVideoRegion = defaults.HotkeyVideoRegion;
        HotkeyVideoFullScreen = defaults.HotkeyVideoFullScreen;
        HotkeyVideoStart = defaults.HotkeyVideoStart;
        HotkeyVideoStop = defaults.HotkeyVideoStop;
        HighlightTrigger = defaults.HighlightTrigger;
    }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string OutputFolder { get; set; }

    /// <summary>Placeholders: %YYYY% %MM% %DD% %hh% %mm% %ss% %NUM%.</summary>
    public string FileNamePattern { get; set; } = Files.FileNamePattern.Default;

    /// <summary>"png" or "jpg".</summary>
    public string Format { get; set; } = "png";

    public int JpegQuality { get; set; } = 90;

    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.OpenEditor;

    public bool CopyPathAfterSave { get; set; }

    public string HotkeyRegion { get; set; }
    public string HotkeyWindow { get; set; }
    public string HotkeyFullScreen { get; set; }
    public string HotkeyLastRegion { get; set; }
    public string HotkeyVideoRegion { get; set; }
    public string HotkeyVideoFullScreen { get; set; }

    /// <summary>Only registered while a recording is being prepared or under way.</summary>
    public string HotkeyVideoStart { get; set; }
    public string HotkeyVideoStop { get; set; }

    /// <summary>Held during a recording to highlight with the mouse: a shortcut, a plain key, or XButton1/XButton2.</summary>
    public string HighlightTrigger { get; set; }

    /// <summary>Empty: the ffmpeg shipped with ShowTime, then the one on the PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Audio input name as ffmpeg lists it; empty for the first microphone found.</summary>
    public string Microphone { get; set; } = "";

    /// <summary>The two audio toggles of the recording bar, remembered from one video to the next.</summary>
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemAudio { get; set; }

    public int VideoFrameRate { get; set; } = 30;

    /// <summary>The recording bar's third toggle: the microphone is transcribed into subtitles after the recording.</summary>
    public bool TranscribeMicrophone { get; set; }

    /// <summary>A Whisper language code such as "fr", or "auto" to detect it.</summary>
    public string TranscriptionLanguage { get; set; } = "fr";

    /// <summary>Empty: the model shipped with ShowTime (in app\models next to ShowTime.exe on Windows).</summary>
    public string TranscriptionModelPath { get; set; } = "";

    public PixelRect? LastRegion { get; set; }

    /// <summary>Next value of %NUM%.</summary>
    public int Counter { get; set; } = 1;

    public string? LastSaveAsFolder { get; set; }

    public string EditorColor { get; set; } = "#E53935";
    public double EditorThickness { get; set; } = 4;
    public double EditorFontSize { get; set; } = 24;

    /// <summary>"png" or "jpg" as a file extension with its dot.</summary>
    [JsonIgnore]
    public string ImageExtension => Format == "jpg" ? ".jpg" : ".png";
}
