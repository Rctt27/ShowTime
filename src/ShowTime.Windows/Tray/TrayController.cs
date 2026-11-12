using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using ShowTime.Adapters;
using ShowTime.Core.Capture;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Core.Updates;
using Forms = System.Windows.Forms;

namespace ShowTime;

/// <summary>The tray menu and the global shortcuts; the captures themselves are the core's use cases.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CaptureWorkflow _captures;
    private readonly RecordingSession _video;
    private readonly IGlobalHotkeys _hotkeys;
    private readonly TrayIcon _tray;
    private readonly IEditorLauncher _editor;
    private readonly IUserDialogs _dialogs;
    private readonly Func<SettingsWindow> _createSettingsWindow;
    private readonly IShell _shell;
    private readonly IFileTrash _trash;
    private readonly Transcriptions _transcriptions;
    private readonly SubtitleFile _subtitles;
    private readonly Updater _updater;

    private readonly Forms.ToolStripMenuItem _regionItem;
    private readonly Forms.ToolStripMenuItem _windowItem;
    private readonly Forms.ToolStripMenuItem _fullScreenItem;
    private readonly Forms.ToolStripMenuItem _lastRegionItem;
    private readonly Forms.ToolStripMenuItem _videoRegionItem;
    private readonly Forms.ToolStripMenuItem _videoFullScreenItem;
    private readonly Forms.ToolStripMenuItem _stopVideoItem;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private readonly Forms.ToolStripSeparator _updateSeparator = new();

    private SettingsWindow? _settingsWindow;

    public TrayController(
        AppSettings settings, CaptureWorkflow captures, RecordingSession video, IGlobalHotkeys hotkeys, TrayIcon tray,
        IEditorLauncher editor, IUserDialogs dialogs, Func<SettingsWindow> createSettingsWindow, IShell shell, IFileTrash trash,
        Transcriptions transcriptions, SubtitleFile subtitles, Updater updater)
    {
        _updater = updater;
        _transcriptions = transcriptions;
        _subtitles = subtitles;
        _shell = shell;
        _trash = trash;
        _settings = settings;
        _captures = captures;
        _video = video;
        _hotkeys = hotkeys;
        _tray = tray;
        _editor = editor;
        _dialogs = dialogs;
        _createSettingsWindow = createSettingsWindow;

        var menu = tray.Menu;
        // First, and only when there is something to say: a newer version, or the manual check when automatic is off.
        _updateItem = AddItem(menu, "", UpdateItem_Click);
        menu.Items.Add(_updateSeparator);
        menu.Opening += (_, _) => _updater.MenuOpening();
        _updater.Changed += (_, _) => UpdateUpdateItem();
        UpdateUpdateItem();
        _regionItem = AddItem(menu, "Capturer une zone", () => _ = _captures.CaptureAreaAsync(windowMode: false, fromMenu: true));
        _windowItem = AddItem(menu, "Capturer une fenêtre", () => _ = _captures.CaptureAreaAsync(windowMode: true, fromMenu: true));
        _fullScreenItem = AddItem(menu, "Capturer tout l'écran", () => _ = _captures.CaptureFullScreenAsync(fromMenu: true));
        _lastRegionItem = AddItem(menu, "Capturer la dernière zone", () => _ = _captures.CaptureLastRegionAsync(fromMenu: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _videoRegionItem = AddItem(menu, "Enregistrer une vidéo (zone)", () => ToggleVideo(fullScreen: false, fromMenu: true));
        _videoFullScreenItem = AddItem(menu, "Enregistrer une vidéo (tout l'écran)", () => ToggleVideo(fullScreen: true, fromMenu: true));
        // "Démarrer" while the bar waits, "Arrêter" during the countdown or the recording.
        _stopVideoItem = AddItem(menu, "Arrêter l'enregistrement", () => _ = _video.ToggleAsync(fullScreen: false, fromMenu: true));
        _stopVideoItem.Font = new Font(_stopVideoItem.Font, System.Drawing.FontStyle.Bold);
        _stopVideoItem.Visible = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "Ouvrir une image…", OpenImageFile);
        AddItem(menu, "Coller depuis le presse-papier", PasteFromClipboard);
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "Ouvrir le dossier des captures", OpenOutputFolder);
        AddItem(menu, "Réglages…", OpenSettings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "Quitter", Quit);

        _video.StateChanged += (_, _) => UpdateVideoState();
        _video.VideoSaved += (_, e) => OpenVideoPreview(e.Path, e.FullScreen, e.MicrophoneTrackPath, e.Transcribe);

        var failures = RegisterHotkeys();
        if (failures.Count > 0)
            ReportHotkeyFailures(failures);
    }

    private static Forms.ToolStripMenuItem AddItem(Forms.ContextMenuStrip menu, string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    /// <summary>Returns the descriptions of the shortcuts Windows refused.</summary>
    private List<string> RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failures = new List<string>();

        void Bind(string text, Forms.ToolStripMenuItem item, Action action)
        {
            item.ShortcutKeyDisplayString = null;
            if (!Hotkey.TryParse(text, out var hotkey))
                return;
            item.ShortcutKeyDisplayString = hotkey.ToString();
            if (!_hotkeys.Register(hotkey, action))
                failures.Add($"{hotkey}  ({item.Text})");
        }

        Bind(_settings.HotkeyRegion, _regionItem, () => _ = _captures.CaptureAreaAsync(windowMode: false, fromMenu: false));
        Bind(_settings.HotkeyWindow, _windowItem, () => _ = _captures.CaptureAreaAsync(windowMode: true, fromMenu: false));
        Bind(_settings.HotkeyFullScreen, _fullScreenItem, () => _ = _captures.CaptureFullScreenAsync(fromMenu: false));
        Bind(_settings.HotkeyLastRegion, _lastRegionItem, () => _ = _captures.CaptureLastRegionAsync(fromMenu: false));
        Bind(_settings.HotkeyVideoRegion, _videoRegionItem, () => ToggleVideo(fullScreen: false, fromMenu: false));
        Bind(_settings.HotkeyVideoFullScreen, _videoFullScreenItem, () => ToggleVideo(fullScreen: true, fromMenu: false));
        return failures;
    }

    private void ReportHotkeyFailures(List<string> failures) =>
        _dialogs.ShowWarning(
            "Windows a refusé ces raccourcis, déjà utilisés par une autre application :\n\n  " +
            string.Join("\n  ", failures) +
            "\n\nCauses fréquentes : un autre outil de capture est encore lancé, ou l'option Windows " +
            "« Utiliser la touche Impr. écran pour ouvrir la capture d'écran » est activée " +
            "(Paramètres > Accessibilité > Clavier).\n\nLes captures restent disponibles depuis l'icône de notification.");

    // ---- Video ----------------------------------------------------------------------------------------------

    private void ToggleVideo(bool fullScreen, bool fromMenu)
    {
        if (!_captures.IsCapturing)
            _ = _video.ToggleAsync(fullScreen, fromMenu);
    }

    /// <summary>
    /// A saved video opens in a player rather than in a notification. Its microphone is transcribed
    /// meanwhile, when the bar's toggle asked for it: the subtitles appear in the player once ready. Otherwise the player
    /// keeps the track for its "Générer les sous-titres" button.
    /// </summary>
    private void OpenVideoPreview(string path, bool fullScreen, string? microphoneTrack, bool transcribe)
    {
        var transcription = microphoneTrack is not null && transcribe ? _transcriptions.Enqueue(path, microphoneTrack) : null;
        var preview = new VideoPreviewWindow(path, fullScreen, _shell, _trash, _subtitles, _transcriptions, transcription,
            transcription is null ? microphoneTrack : null);
        preview.RetakeRequested += () => Retake(preview, fullScreen);
        preview.Show();
        preview.Activate();
    }

    /// <summary>"Refaire": discards the video, then records the same way again; an area is selected anew.</summary>
    private async void Retake(VideoPreviewWindow preview, bool fullScreen)
    {
        // Toggle would act on the session under way instead: start its countdown, or stop it.
        if (_captures.IsCapturing || _video.IsBusy)
        {
            MessageBox.Show(preview, "Un enregistrement est déjà en préparation ou en cours.", "ShowTime",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await preview.DiscardAsync() || _captures.IsCapturing || _video.IsBusy)
            return;
        // As from the menu: the closed preview fades out before the screen is frozen for the selection.
        ToggleVideo(fullScreen, fromMenu: true);
    }

    private void UpdateVideoState()
    {
        // From the area selection on, a preview still playing would end up in the new video's sound.
        if (_video.IsBusy)
            foreach (var preview in Application.Current.Windows.OfType<VideoPreviewWindow>())
                preview.PauseIfPlaying();

        _stopVideoItem.Visible = _video.IsReady || _video.IsRecording;
        _stopVideoItem.Text = _video.IsReady ? "Démarrer l'enregistrement" : "Arrêter l'enregistrement";
        _videoRegionItem.Enabled = _videoFullScreenItem.Enabled = !_video.IsBusy;
        UpdateTrayText();
    }

    // ---- Updates ----------------------------------------------------------------------------------------

    private void UpdateUpdateItem()
    {
        var entry = UpdateMenuEntry.For(_updater.State, _settings.CheckForUpdates);
        _updateItem.Text = entry.Text;
        _updateItem.Enabled = entry.Enabled;
        _updateItem.Visible = _updateSeparator.Visible = entry.Visible;
        UpdateTrayText();
    }

    private void UpdateTrayText() =>
        _tray.Text = _video.IsRecording ? "ShowTime — enregistrement en cours" : UpdateMenuEntry.ToolTip(_updater.State) ?? "ShowTime";

    private void UpdateItem_Click()
    {
        if (_updater.State.Stage is UpdateStage.Available or UpdateStage.Downloaded)
            _ = _updater.InstallAsync();
        else
            _ = _updater.CheckNowAsync();
    }

    // ---- Other menu entries ---------------------------------------------------------------------------------

    private void OpenImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Tous les fichiers|*.*",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(dialog.FileName);
            image.EndInit();
            _editor.Open(Normalize(image));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            _dialogs.ShowError("Impossible d'ouvrir cette image : " + ex.Message);
        }
    }

    private void PasteFromClipboard()
    {
        BitmapSource? image;
        try
        {
            image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _dialogs.ShowWarning("Lecture du presse-papier impossible : " + ex.Message);
            return;
        }

        if (image is null)
            _tray.Info("Presse-papier vide", "Le presse-papier ne contient pas d'image.");
        else
            _editor.Open(Normalize(image));
    }

    /// <summary>Any image (file, clipboard) at 96 DPI, so that one WPF unit is one pixel in the editor.</summary>
    private static WpfImage Normalize(BitmapSource image) => WpfImage.FromPixels(WpfImage.ToPixels(image));

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            _shell.OpenFolder(_settings.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowWarning("Impossible d'ouvrir le dossier des captures : " + ex.Message);
        }
    }

    /// <summary>
    /// Closes the editors one by one first: during Application.Shutdown, cancelling the "save before closing?"
    /// prompt would be ignored and the capture lost.
    /// </summary>
    private async void Quit()
    {
        if (_video.IsRecording)
        {
            var answer = await _dialogs.AskYesNoCancelAsync("Un enregistrement vidéo est en cours.\n\nL'arrêter et garder la vidéo avant de quitter ?");
            if (answer == DialogAnswer.Cancel)
                return;
            if (answer == DialogAnswer.Yes)
                await _video.StopAsync();
        }
        // A recording prepared but not started, or a countdown, has nothing to keep.
        _video.Cancel();
        // A stop already under way (from the recording bar, for instance) is awaited rather than ignored.
        await _video.StopAsync();
        if (_video.IsBusy)
        {
            _dialogs.ShowInfo("Terminez d'abord la sélection de la zone à filmer.");
            return;
        }

        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            if (!closed)
                return;
        }
        Application.Current.Shutdown();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // While editing shortcuts, pressing one must reach the settings window instead of triggering a capture.
        _hotkeys.UnregisterAll();
        _settingsWindow = _createSettingsWindow();
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // The automatic check may have been turned on or off.
            UpdateUpdateItem();
            var failures = RegisterHotkeys();
            if (failures.Count > 0)
                ReportHotkeyFailures(failures);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose() => _hotkeys.UnregisterAll();
}

/// <summary>Opens each capture in its own editor window.</summary>
internal sealed class WpfEditorLauncher(EditorContext context) : IEditorLauncher
{
    public void Open(IImage image)
    {
        var editor = new EditorWindow(WpfImage.Unwrap(image), context);
        editor.Show();
        editor.Activate();
    }
}
