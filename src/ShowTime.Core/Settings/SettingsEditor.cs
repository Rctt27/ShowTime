using System.IO.Abstractions;

namespace ShowTime.Core.Settings;

/// <summary>The fields of the settings window, as the user typed them.</summary>
public sealed record SettingsForm(
    string OutputFolder,
    string FileNamePattern,
    string Format,
    int JpegQuality,
    bool CopyPathAfterSave,
    AfterCaptureAction AfterCapture,
    string HotkeyRegion,
    string HotkeyWindow,
    string HotkeyFullScreen,
    string HotkeyLastRegion,
    string HotkeyVideoRegion,
    string HotkeyVideoFullScreen,
    string HotkeyVideoStart,
    string HotkeyVideoStop,
    string HighlightTrigger,
    string FfmpegPath,
    string Microphone,
    bool RecordMicrophone,
    bool RecordSystemAudio,
    int VideoFrameRate,
    bool TranscribeMicrophone,
    string TranscriptionLanguage,
    string TranscriptionModelPath,
    HighlightStyle HighlightStyle)
{
    /// <summary>The frame rates offered.</summary>
    public static IReadOnlyList<int> FrameRates { get; } = [15, 24, 30, 60];

    /// <summary>The transcription languages offered, by Whisper code, with their French names.</summary>
    public static IReadOnlyList<(string Code, string Name)> TranscriptionLanguages { get; } =
        [("fr", "Français"), ("en", "Anglais"), ("auto", "Détection automatique")];

    public IEnumerable<string> Hotkeys =>
    [
        HotkeyRegion, HotkeyWindow, HotkeyFullScreen, HotkeyLastRegion, HotkeyVideoRegion, HotkeyVideoFullScreen,
        HotkeyVideoStart, HotkeyVideoStop, HighlightTrigger
    ];

    public static SettingsForm From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsForm(
            settings.OutputFolder, settings.FileNamePattern, settings.Format, settings.JpegQuality, settings.CopyPathAfterSave,
            settings.AfterCapture, settings.HotkeyRegion, settings.HotkeyWindow, settings.HotkeyFullScreen, settings.HotkeyLastRegion,
            settings.HotkeyVideoRegion, settings.HotkeyVideoFullScreen, settings.HotkeyVideoStart, settings.HotkeyVideoStop,
            settings.HighlightTrigger, settings.FfmpegPath, settings.Microphone, settings.RecordMicrophone, settings.RecordSystemAudio,
            FrameRates.OrderBy(f => Math.Abs(f - settings.VideoFrameRate)).First(),
            settings.TranscribeMicrophone,
            TranscriptionLanguages.Any(l => l.Code == settings.TranscriptionLanguage) ? settings.TranscriptionLanguage : "fr",
            settings.TranscriptionModelPath,
            settings.HighlightStyle);
    }

    /// <summary>Copies the fields, trimmed, into the settings.</summary>
    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.OutputFolder = OutputFolder.Trim();
        settings.FileNamePattern = FileNamePattern.Trim();
        settings.Format = Format;
        settings.JpegQuality = JpegQuality;
        settings.CopyPathAfterSave = CopyPathAfterSave;
        settings.AfterCapture = AfterCapture;
        settings.HotkeyRegion = HotkeyRegion.Trim();
        settings.HotkeyWindow = HotkeyWindow.Trim();
        settings.HotkeyFullScreen = HotkeyFullScreen.Trim();
        settings.HotkeyLastRegion = HotkeyLastRegion.Trim();
        settings.HotkeyVideoRegion = HotkeyVideoRegion.Trim();
        settings.HotkeyVideoFullScreen = HotkeyVideoFullScreen.Trim();
        settings.HotkeyVideoStart = HotkeyVideoStart.Trim();
        settings.HotkeyVideoStop = HotkeyVideoStop.Trim();
        settings.HighlightTrigger = HighlightTrigger.Trim();
        settings.FfmpegPath = FfmpegPath.Trim();
        settings.Microphone = Microphone;
        settings.RecordMicrophone = RecordMicrophone;
        settings.RecordSystemAudio = RecordSystemAudio;
        settings.VideoFrameRate = VideoFrameRate;
        settings.TranscribeMicrophone = TranscribeMicrophone;
        settings.TranscriptionLanguage = TranscriptionLanguage;
        settings.TranscriptionModelPath = TranscriptionModelPath.Trim();
        settings.HighlightStyle = HighlightStyle.Clamped();
    }
}

/// <summary>Checks and saves what the settings window holds.</summary>
/// <param name="folderExample">A full folder path in this platform's form, shown when a relative one is typed.</param>
public sealed class SettingsEditor(AppSettings settings, ISettingsStore store, IFileSystem fileSystem, string folderExample)
{
    /// <returns>Null once applied and saved, otherwise the message explaining what to correct.</returns>
    public string? TryApply(SettingsForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var duplicate = form.Hotkeys.Select(h => h.Trim()).Where(h => h.Length > 0)
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return $"Le raccourci {duplicate.Key} est attribué deux fois.";
        if (string.IsNullOrWhiteSpace(form.OutputFolder) || string.IsNullOrWhiteSpace(form.FileNamePattern))
            return "Le dossier et le nom des fichiers sont obligatoires.";

        var ffmpegPath = form.FfmpegPath.Trim();
        if (ffmpegPath.Length > 0 && !fileSystem.File.Exists(ffmpegPath))
            return "Le fichier ffmpeg indiqué n'existe pas. Laissez le champ vide pour utiliser celui livré avec ShowTime.";
        var modelPath = form.TranscriptionModelPath.Trim();
        if (modelPath.Length > 0 && !fileSystem.File.Exists(modelPath))
            return "Le modèle de transcription indiqué n'existe pas. Laissez le champ vide pour utiliser celui livré avec ShowTime.";

        // A relative path would depend on the working directory, which is System32 when Windows starts the app.
        var folder = form.OutputFolder.Trim();
        if (!fileSystem.Path.IsPathFullyQualified(folder))
            return $"Indiquez le chemin complet du dossier, par exemple {folderExample}.";
        try
        {
            folder = fileSystem.Path.GetFullPath(folder);
            fileSystem.Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "Ce dossier est inutilisable : " + ex.Message;
        }

        (form with { OutputFolder = folder }).ApplyTo(settings);
        try
        {
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Les réglages n'ont pas pu être enregistrés : " + ex.Message;
        }
        return null;
    }
}
