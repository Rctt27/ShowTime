using AppKit;
using AVFoundation;
using Foundation;
using ShowTime.Core.Capture;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Mac.Adapters;
using ShowTime.Mac.Editor;
using ShowTime.Mac.Platform;
using ShowTime.Mac.Settings;
using ShowTime.Mac.Video;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac;

/// <summary>
/// ShowTime's icon in the menu bar, its menu and the global shortcuts, as the tray icon on Windows; the captures
/// themselves are the core's use cases.
/// </summary>
internal sealed class StatusMenu : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CaptureWorkflow _captures;
    private readonly IGlobalHotkeys _hotkeys;
    private readonly IUserDialogs _dialogs;
    private readonly IShell _shell;
    private readonly MacEditorLauncher _editor;
    private readonly INotifications _notifications;
    private readonly RecordingSession _video;
    private readonly IFileTrash _trash;
    private readonly SubtitleFile _subtitles;
    private readonly Transcriptions _transcriptions;
    private readonly List<VideoPreviewWindowController> _previews = [];
    private readonly Func<SettingsWindowController> _createSettingsWindow;
    private SettingsWindowController? _settingsWindow;

    private readonly NSStatusItem _statusItem;
    private readonly NSMenuItem _regionItem;
    private readonly NSMenuItem _windowItem;
    private readonly NSMenuItem _fullScreenItem;
    private readonly NSMenuItem _lastRegionItem;
    private readonly NSMenuItem _videoRegionItem;
    private readonly NSMenuItem _videoFullScreenItem;
    private readonly NSMenuItem _stopVideoItem;

    public StatusMenu(AppSettings settings, CaptureWorkflow captures, IGlobalHotkeys hotkeys, IUserDialogs dialogs, IShell shell,
        MacEditorLauncher editor, INotifications notifications, RecordingSession video, IFileTrash trash,
        SubtitleFile subtitles, Transcriptions transcriptions, Func<SettingsWindowController> createSettingsWindow)
    {
        _settings = settings;
        _captures = captures;
        _hotkeys = hotkeys;
        _dialogs = dialogs;
        _shell = shell;
        _editor = editor;
        _notifications = notifications;
        _video = video;
        _trash = trash;
        _subtitles = subtitles;
        _transcriptions = transcriptions;
        _createSettingsWindow = createSettingsWindow;

        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem("ShowTime " + Version) { Enabled = false });
        menu.AddItem(NSMenuItem.SeparatorItem);
        _regionItem = AddItem(menu, "Capturer une zone", () => _ = _captures.CaptureAreaAsync(windowMode: false, fromMenu: true));
        _windowItem = AddItem(menu, "Capturer une fenêtre", () => _ = _captures.CaptureAreaAsync(windowMode: true, fromMenu: true));
        _fullScreenItem = AddItem(menu, "Capturer tout l'écran", () => _ = _captures.CaptureFullScreenAsync(fromMenu: true));
        _lastRegionItem = AddItem(menu, "Capturer la dernière zone", () => _ = _captures.CaptureLastRegionAsync(fromMenu: true));
        menu.AddItem(NSMenuItem.SeparatorItem);
        _videoRegionItem = AddItem(menu, "Enregistrer une vidéo (zone)", () => _ = ToggleVideoAsync(fullScreen: false, fromMenu: true));
        _videoFullScreenItem = AddItem(menu, "Enregistrer une vidéo (tout l'écran)", () => _ = ToggleVideoAsync(fullScreen: true, fromMenu: true));
        // "Démarrer" while the bar waits, "Arrêter" during the countdown or the recording.
        _stopVideoItem = AddItem(menu, "Arrêter l'enregistrement", () => _ = _video.ToggleAsync(fullScreen: false, fromMenu: true));
        _stopVideoItem.Hidden = true;
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddItem(menu, "Ouvrir une image…", OpenImageFile);
        AddItem(menu, "Coller depuis le presse-papiers", PasteFromClipboard);
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddItem(menu, "Ouvrir le dossier des captures", OpenOutputFolder);
        AddItem(menu, "Réglages…", OpenSettings);
        menu.AddItem(NSMenuItem.SeparatorItem);
        AddItem(menu, "Quitter ShowTime", () => NSApplication.SharedApplication.Terminate(NSApplication.SharedApplication));

        _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
        if (_statusItem.Button is { } button)
        {
            if (NSImage.ImageNamed("StatusIcon") is { } icon)
            {
                icon.Template = true; // drawn by macOS in the menu bar's colour, light or dark
                button.Image = icon;
            }
            button.ToolTip = "ShowTime";
        }
        _statusItem.Menu = menu;

        _video.StateChanged += (_, _) => UpdateVideoState();
        _video.VideoSaved += (_, e) => OpenVideoPreview(e.Path, e.FullScreen, e.MicrophoneTrackPath, e.Transcribe);

        var failures = RegisterHotkeys();
        if (failures.Count > 0)
            ReportHotkeyFailures(failures);
    }

    private static string Version =>
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "";

    private static NSMenuItem AddItem(NSMenu menu, string title, Action action)
    {
        var item = new NSMenuItem(title, (_, _) => action());
        menu.AddItem(item);
        return item;
    }

    /// <summary>Returns the descriptions of the shortcuts macOS refused.</summary>
    private List<string> RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failures = new List<string>();

        void Bind(string text, NSMenuItem item, Action action)
        {
            item.KeyEquivalent = "";
            if (!Hotkey.TryParse(text, out var hotkey))
                return;
            // Shown in the menu, as macOS shows its own shortcuts.
            if (MacKeys.KeyEquivalent(hotkey.Key) is { } equivalent)
            {
                item.KeyEquivalent = equivalent;
                item.KeyEquivalentModifierMask = Mask(hotkey.Modifiers);
            }
            if (!_hotkeys.Register(hotkey, action))
                failures.Add($"{hotkey.Format(HotkeyNaming.MacOS)}  ({item.Title})");
        }

        Bind(_settings.HotkeyRegion, _regionItem, () => _ = _captures.CaptureAreaAsync(windowMode: false, fromMenu: false));
        Bind(_settings.HotkeyWindow, _windowItem, () => _ = _captures.CaptureAreaAsync(windowMode: true, fromMenu: false));
        Bind(_settings.HotkeyFullScreen, _fullScreenItem, () => _ = _captures.CaptureFullScreenAsync(fromMenu: false));
        Bind(_settings.HotkeyLastRegion, _lastRegionItem, () => _ = _captures.CaptureLastRegionAsync(fromMenu: false));
        Bind(_settings.HotkeyVideoRegion, _videoRegionItem, () => _ = ToggleVideoAsync(fullScreen: false, fromMenu: false));
        Bind(_settings.HotkeyVideoFullScreen, _videoFullScreenItem, () => _ = ToggleVideoAsync(fullScreen: true, fromMenu: false));
        return failures;
    }

    private static NSEventModifierMask Mask(HotkeyModifiers modifiers) =>
        (modifiers.HasFlag(HotkeyModifiers.Control) ? NSEventModifierMask.ControlKeyMask : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Alt) ? NSEventModifierMask.AlternateKeyMask : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Shift) ? NSEventModifierMask.ShiftKeyMask : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Meta) ? NSEventModifierMask.CommandKeyMask : 0);

    private void ReportHotkeyFailures(List<string> failures) =>
        _dialogs.ShowWarning(
            "macOS a refusé ces raccourcis, déjà utilisés par une autre application ou absents d'un clavier de Mac :\n\n  " +
            string.Join("\n  ", failures) +
            "\n\nLes captures restent disponibles depuis l'icône de ShowTime dans la barre des menus.");

    // ---- Video ----------------------------------------------------------------------------------------------

    private async Task ToggleVideoAsync(bool fullScreen, bool fromMenu)
    {
        if (_captures.IsCapturing)
            return;
        // ffmpeg records the screen on ShowTime's behalf. Without the permission, a video of the whole screen would
        // only show the wallpaper (an area is checked by its selection, which captures the screen).
        if (!_video.IsBusy && !ScreenCaptureAccess.IsGranted)
        {
            ScreenCaptureAccess.Request();
            _dialogs.ShowWarning(MacScreenGrabber.PermissionMessage);
            return;
        }
        // ShowTime records the microphone itself: macOS asks, once, before the first video.
        if (!_video.IsBusy && _settings.RecordMicrophone &&
            AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio) == AVAuthorizationStatus.NotDetermined)
            await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio).ConfigureAwait(true);
        await _video.ToggleAsync(fullScreen, fromMenu).ConfigureAwait(true);
    }

    /// <summary>
    /// A saved video opens in a player rather than in a notification; its microphone is transcribed
    /// straight away when asked, otherwise the track waits for the player's "Générer les sous-titres".
    /// </summary>
    private void OpenVideoPreview(string path, bool fullScreen, string? microphoneTrack, bool transcribe)
    {
        var transcription = microphoneTrack is not null && transcribe ? _transcriptions.Enqueue(path, microphoneTrack) : null;
        var preview = new VideoPreviewWindowController(path, _shell, _trash, _subtitles, _transcriptions, transcription,
            transcription is null ? microphoneTrack : null);
        _previews.Add(preview);
        preview.RetakeRequested += (_, _) => Retake(preview, fullScreen);
        preview.Closed += (_, _) =>
        {
            _previews.Remove(preview);
            // Released once AppKit has finished closing the window.
            CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(preview.Dispose);
        };
        preview.Show();
    }

    /// <summary>"Refaire": discards the video, then records the same way again; an area is selected anew.</summary>
    private void Retake(VideoPreviewWindowController preview, bool fullScreen)
    {
        // Toggle would act on the session under way instead: start its countdown, or stop it.
        if (_captures.IsCapturing || _video.IsBusy)
        {
            _dialogs.ShowInfo("Un enregistrement est déjà en préparation ou en cours.");
            return;
        }
        if (!preview.Discard() || _captures.IsCapturing || _video.IsBusy)
            return;
        // As from the menu: the closed preview fades out before the screen is frozen for the selection.
        _ = ToggleVideoAsync(fullScreen, fromMenu: true);
    }

    private void UpdateVideoState()
    {
        // From the area selection on, a preview still playing would end up in the new video's sound.
        if (_video.IsBusy)
            foreach (var preview in _previews)
                preview.PauseIfPlaying();

        _stopVideoItem.Hidden = !(_video.IsReady || _video.IsRecording);
        _stopVideoItem.Title = _video.IsReady ? "Démarrer l'enregistrement" : "Arrêter l'enregistrement";
        _videoRegionItem.Enabled = _videoFullScreenItem.Enabled = !_video.IsBusy;
        if (_statusItem.Button is { } button)
            button.ToolTip = _video.IsRecording ? "ShowTime — enregistrement en cours" : "ShowTime";
    }

    // ---- Quitting -------------------------------------------------------------------------------------------

    /// <summary>
    /// Asked by macOS before quitting, from the menu or ⌘Q: a recording is stopped and kept, or the quit cancelled;
    /// then every unsaved capture asks to be saved. The answer comes once the recording has stopped.
    /// </summary>
    public NSApplicationTerminateReply ShouldTerminate()
    {
        // Nothing to wait for: the answer is given now.
        if (!_video.IsBusy)
            return _editor.CloseAll() ? NSApplicationTerminateReply.Now : NSApplicationTerminateReply.Cancel;
        // A recording stops asynchronously: Later, and the reply once this call has returned, as AppKit expects.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() => _ = ReplyToTerminateAsync());
        return NSApplicationTerminateReply.Later;
    }

    private async Task ReplyToTerminateAsync()
    {
        var quit = false;
        try
        {
            quit = await PrepareToQuitAsync().ConfigureAwait(true);
        }
        finally
        {
            NSApplication.SharedApplication.ReplyToApplicationShouldTerminate(quit);
        }
    }

    private async Task<bool> PrepareToQuitAsync()
    {
        if (_video.IsRecording)
        {
            var answer = MacDialogs.AskYesNoCancel("Un enregistrement vidéo est en cours.\n\nL'arrêter et garder la vidéo avant de quitter ?");
            if (answer == DialogAnswer.Cancel)
                return false;
            if (answer == DialogAnswer.Yes)
                await _video.StopAsync().ConfigureAwait(true);
        }
        // A recording prepared but not started, or a countdown, has nothing to keep.
        _video.Cancel();
        // A stop already under way (from the recording bar, for instance) is awaited rather than ignored.
        await _video.StopAsync().ConfigureAwait(true);
        if (_video.IsBusy)
        {
            _dialogs.ShowInfo("Terminez d'abord la sélection de la zone à filmer.");
            return false;
        }
        return _editor.CloseAll();
    }

    // ---- Other menu entries ---------------------------------------------------------------------------------

    private void OpenImageFile()
    {
        if (MacDialogs.AskImageToOpen(_settings.OutputFolder) is not { } path)
            return;
        if (MacImage.Load(path) is { } image)
            _editor.Open(image);
        else
            _dialogs.ShowError("Impossible d'ouvrir cette image : macOS ne sait pas la lire.");
    }

    private void PasteFromClipboard()
    {
        if (MacClipboard.GetImage() is { } image)
            _editor.Open(image);
        else
            _notifications.Info("Presse-papiers vide", "Le presse-papiers ne contient pas d'image.");
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            return;
        }

        // While editing shortcuts, pressing one must reach the settings window instead of triggering a capture.
        _hotkeys.UnregisterAll();
        var window = _settingsWindow = _createSettingsWindow();
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // Released once AppKit has finished closing the window.
            CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(window.Dispose);
            var failures = RegisterHotkeys();
            if (failures.Count > 0)
                ReportHotkeyFailures(failures);
        };
        window.Show();
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            _shell.OpenFolder(_settings.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowWarning("Impossible d'ouvrir le dossier des captures : " + ex.Message);
        }
    }

    public void Dispose()
    {
        _hotkeys.UnregisterAll();
        NSStatusBar.SystemStatusBar.RemoveStatusItem(_statusItem);
        _statusItem.Dispose();
    }
}
