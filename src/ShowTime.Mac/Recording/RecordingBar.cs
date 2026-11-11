using System.Diagnostics;
using System.Globalization;
using AppKit;
using CoreGraphics;
using Foundation;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Mac.Platform;

namespace ShowTime.Mac.Recording;

/// <summary>
/// Audio toggles and Start before recording, then elapsed time, Stop and Cancel. A panel that never takes the focus
/// from the application being filmed, can be dragged anywhere, and is excluded from the video.
/// </summary>
internal sealed class RecordingBar : NSPanel
{
    private static readonly NSColor Red = NSColor.FromSrgb(229f / 255, 57f / 255, 53f / 255, 1f);

    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly IFfmpegPlatform _platform;
    private readonly Stopwatch _elapsed = new();
    private readonly NSStackView _stack;
    private readonly NSTextField _dot = NSTextField.CreateLabel("●");
    private readonly NSTextField _status = NSTextField.CreateLabel("");
    private readonly NSTextField _highlight = NSTextField.CreateLabel("");
    private readonly NSButton _microphone;
    private readonly NSPopUpButton _microphoneBox = new(CGRect.Empty, false);
    private readonly NSButton _systemAudio;
    private readonly NSButton _transcription;
    private readonly bool _canTranscribe;
    private readonly NSButton _start;
    private readonly NSButton _stop;
    private readonly NSButton _cancel;
    private NSTimer? _timer;
    private bool _hasMicrophone = true;
    private bool _hasSystemAudio = true;
    private bool _audioLocked;

    /// <param name="canTranscribe">Whether there is a model to transcribe the microphone with.</param>
    public RecordingBar(AppSettings settings, ISettingsStore store, IFfmpegPlatform platform, INativeWindows windows, bool canTranscribe)
        : base(new CGRect(0, 0, 400, 44), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
    {
        _settings = settings;
        _store = store;
        _platform = platform;
        _canTranscribe = canTranscribe;
        ReleaseWhenClosed(false);
        FloatingPanel = true;
        HidesOnDeactivate = false;
        MovableByWindowBackground = true;
        IsOpaque = false;
        BackgroundColor = NSColor.Clear;
        HasShadow = true;
        Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);

        _dot.TextColor = Red;
        if (NSFont.MonospacedDigitSystemFontOfSize(13, NSFontWeight.Medium) is { } font)
            _status.Font = font;
        // Wide enough for "Enregistrement  00:00.0", so that the bar does not resize while the counter runs.
        _status.WidthAnchor.ConstraintGreaterThanOrEqualTo(190).Active = true;
        _highlight.TextColor = NSColor.SecondaryLabel;
        _highlight.Hidden = true;
        _highlight.ToolTip = "Une traînée jaune suit la souris tant que le raccourci est maintenu ; elle apparaît dans la vidéo";
        _microphone = Toggle("Micro", Microphone_Changed);
        _systemAudio = Toggle("Son du Mac", SystemAudio_Changed);
        _transcription = Toggle("Sous-titres", Transcription_Changed);
        _microphoneBox.ToolTip = "Micro utilisé";
        _microphoneBox.Activated += (_, _) =>
        {
            if (_microphoneBox.TitleOfSelectedItem is { Length: > 0 } microphone)
            {
                _settings.Microphone = microphone;
                _store.TrySave(_settings);
            }
        };
        _start = NSButton.CreateButton("●  Démarrer", () => StartRequested?.Invoke(this, EventArgs.Empty));
        _start.ToolTip = "Lancer le compte à rebours de 3 secondes, puis l'enregistrement";
        _stop = NSButton.CreateButton("■  Arrêter", () => StopRequested?.Invoke(this, EventArgs.Empty));
        _stop.ToolTip = "Arrêter et enregistrer la vidéo";
        _stop.Hidden = true;
        _cancel = NSButton.CreateButton("Annuler", () => CancelRequested?.Invoke(this, EventArgs.Empty));
        _cancel.ToolTip = "Abandonner : rien n'est enregistré";

        _stack = NSStackView.FromViews([_dot, _status, _highlight, _microphone, _microphoneBox, _systemAudio, _transcription, _start, _stop, _cancel]);
        _stack.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        _stack.Spacing = 8;
        _stack.EdgeInsets = new NSEdgeInsets(6, 12, 6, 12);
        _stack.ToolTip = "Glisser pour déplacer · invisible dans la vidéo";
        var background = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.HudWindow,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.Active,
            WantsLayer = true
        };
        background.Layer!.CornerRadius = 10;
        background.Layer.MasksToBounds = true;
        background.AddSubview(_stack);
        _stack.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.LeadingAnchor.ConstraintEqualTo(background.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(background.TrailingAnchor),
            _stack.TopAnchor.ConstraintEqualTo(background.TopAnchor),
            _stack.BottomAnchor.ConstraintEqualTo(background.BottomAnchor)
        ]);
        ContentView = background;

        windows.ExcludeFromCapture(Handle);
        windows.RaiseAboveEverything(Handle);
        UpdateAudioToggles();
    }

    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? CancelRequested;

    /// <summary>Clicks on the bar work without bringing ShowTime to the front.</summary>
    public override bool CanBecomeKeyWindow => false;

    /// <summary>Below the recorded area when there is room, otherwise above it, otherwise inside it.</summary>
    public void ShowNear(PixelRect area)
    {
        var screen = ScreenInfo.Holding(area);
        var size = Fit();
        var scale = screen.Frame.Scale;
        var bar = new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        var position = RecordingBarPlacement.Place(area, bar, screen.WorkArea);
        var frame = screen.ToCocoa(new PixelRect(position.X, position.Y, bar.Width, bar.Height));
        SetFrameOrigin(frame.Location);
        OrderFrontRegardless();
    }

    public void ShowReady(string? startShortcut, string? stopShortcut)
    {
        _dot.AlphaValue = 0.35f;
        _status.StringValue = "Prêt à enregistrer";
        _start.Title = startShortcut is null ? "●  Démarrer" : $"●  Démarrer ({startShortcut})";
        _stop.Title = stopShortcut is null ? "■  Arrêter" : $"■  Arrêter ({stopShortcut})";
        _start.Hidden = false;
        _stop.Hidden = true;
        _start.Enabled = true;
        _highlight.Hidden = true;
        _audioLocked = false;
        UpdateAudioToggles();
    }

    /// <summary>Enables what this computer can record, once ffmpeg has listed the audio inputs.</summary>
    public void SetAudioDevices(IReadOnlyCollection<string> devices)
    {
        // An empty list means ffmpeg could not list them: the saved choices are then left as they are.
        if (devices.Count == 0)
            return;
        var microphones = AudioDevices.Microphones(devices, _platform.SystemAudioDevice).ToList();
        _hasMicrophone = microphones.Count > 0;
        _hasSystemAudio = _platform.CanRecordSystemAudio(devices);
        _microphoneBox.RemoveAllItems();
        _microphoneBox.AddItems([.. microphones]);
        if (AudioDevices.PickMicrophone(devices, _settings.Microphone, _platform.SystemAudioDevice) is { } picked)
            _microphoneBox.SelectItem(picked);
        UpdateAudioToggles();
    }

    public void ShowCountdown(int seconds)
    {
        _status.StringValue = string.Create(CultureInfo.InvariantCulture, $"Début dans {seconds}…");
        _start.Enabled = false;
        _audioLocked = true;
        UpdateAudioToggles();
    }

    /// <summary>ffmpeg is launched but not capturing yet: Stop already works, the counter waits.</summary>
    public void ShowStarting()
    {
        _status.StringValue = "Démarrage…";
        ShowStop();
    }

    /// <summary>Capture has begun: what is said from now on is in the video.</summary>
    /// <remarks>
    /// Usually straight from the countdown, without <see cref="ShowStarting"/>: ffmpeg captures before 0. Stop must
    /// then appear here too, otherwise only the shortcut could end the recording.
    /// </remarks>
    public void ShowRecording()
    {
        ShowStop();
        _dot.AlphaValue = 1;
        _elapsed.Start();
        _status.StringValue = "Enregistrement  00:00.0";
        _timer = NSTimer.CreateRepeatingScheduledTimer(0.1, _ =>
        {
            var elapsed = _elapsed.Elapsed;
            _status.StringValue = string.Create(CultureInfo.InvariantCulture, $"Enregistrement  {elapsed:mm\\:ss\\.f}");
            // The timer ticks for the tenths; the dot keeps blinking every half second.
            _dot.AlphaValue = (long)elapsed.TotalMilliseconds / 500 % 2 == 0 ? 1 : 0.35f;
        });
    }

    /// <summary>During the recording: the shortcut to hold to highlight, or null when there is none.</summary>
    public void ShowHighlightShortcut(string? shortcut)
    {
        _highlight.StringValue = shortcut is null ? "" : $"Surligner : maintenir {shortcut}";
        _highlight.Hidden = shortcut is null;
        Fit();
    }

    public void ShowFinishing()
    {
        StopTimer();
        _highlight.Hidden = true;
        Fit();
        _dot.AlphaValue = 0.35f;
        _status.StringValue = "Finalisation…";
        _stop.Enabled = _cancel.Enabled = false;
    }

    public void CloseBar()
    {
        StopTimer();
        OrderOut(null);
        Close();
    }

    private void ShowStop()
    {
        _start.Hidden = true;
        _stop.Hidden = false;
        Fit();
    }

    private void StopTimer()
    {
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Sizes the bar to its content, keeping its top edge where it is, and back inside its screen: placed against the
    /// right edge, the bar would otherwise push Arrêter and Annuler off screen when the highlight hint appears.
    /// </summary>
    private CGSize Fit()
    {
        _stack.LayoutSubtreeIfNeeded();
        var size = _stack.FittingSize;
        var frame = Frame;
        var x = frame.X;
        if (Screen?.VisibleFrame is { } visible)
            x = nfloat.Max(visible.X, nfloat.Min(x, visible.X + visible.Width - size.Width));
        SetFrame(new CGRect(x, frame.Y + frame.Height - size.Height, size.Width, size.Height), true);
        return size;
    }

    private static NSButton Toggle(string title, Action changed)
    {
        var button = NSButton.CreateButton(title, changed);
        button.SetButtonType(NSButtonType.PushOnPushOff);
        return button;
    }

    /// <summary>
    /// The toggles show the saved choice, except for an input this computer lacks. Once the countdown starts, ffmpeg's
    /// inputs are set: the toggles are locked.
    /// </summary>
    private void UpdateAudioToggles()
    {
        var microphoneOn = _hasMicrophone && _settings.RecordMicrophone;
        _microphone.State = microphoneOn ? NSCellStateValue.On : NSCellStateValue.Off;
        _microphone.Enabled = _hasMicrophone && !_audioLocked;
        SetSymbol(_microphone, microphoneOn ? "mic.fill" : "mic.slash");
        _microphone.ToolTip = !_hasMicrophone ? "Aucun micro détecté"
            : _settings.RecordMicrophone ? "Le micro est enregistré. Cliquer pour le couper." : "Micro coupé. Cliquer pour l'enregistrer.";
        _microphoneBox.Hidden = !microphoneOn || _microphoneBox.ItemCount < 2;
        _microphoneBox.Enabled = !_audioLocked;

        var systemOn = _hasSystemAudio && _settings.RecordSystemAudio;
        _systemAudio.State = systemOn ? NSCellStateValue.On : NSCellStateValue.Off;
        _systemAudio.Enabled = _hasSystemAudio && !_audioLocked;
        SetSymbol(_systemAudio, systemOn ? "speaker.wave.2.fill" : "speaker.slash");
        _systemAudio.ToolTip = !_hasSystemAudio
            ? "Nécessite " + _platform.SystemAudioRequirement + " : introuvable sur ce Mac."
            : _settings.RecordSystemAudio ? "Le son du Mac est enregistré. Cliquer pour le couper."
                : "Son du Mac coupé. Cliquer pour l'enregistrer.";

        var transcription = SubtitleToggle.For(_canTranscribe, microphoneOn, _settings.TranscribeMicrophone, _audioLocked);
        _transcription.State = transcription.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
        _transcription.Enabled = transcription.IsEnabled;
        SetSymbol(_transcription, transcription.IsChecked ? "captions.bubble.fill" : "captions.bubble");
        _transcription.ToolTip = transcription.ToolTip;
        Fit();
    }

    private static void SetSymbol(NSButton button, string name)
    {
        if (NSImage.GetSystemSymbol(name, null) is { } image)
            button.Image = image;
    }

    private void Microphone_Changed()
    {
        _settings.RecordMicrophone = _microphone.State == NSCellStateValue.On;
        _store.TrySave(_settings);
        UpdateAudioToggles();
    }

    private void SystemAudio_Changed()
    {
        _settings.RecordSystemAudio = _systemAudio.State == NSCellStateValue.On;
        _store.TrySave(_settings);
        UpdateAudioToggles();
    }

    private void Transcription_Changed()
    {
        _settings.TranscribeMicrophone = _transcription.State == NSCellStateValue.On;
        _store.TrySave(_settings);
        UpdateAudioToggles();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopTimer();
        base.Dispose(disposing);
    }
}
