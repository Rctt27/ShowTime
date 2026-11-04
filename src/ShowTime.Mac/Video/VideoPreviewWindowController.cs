using AppKit;
using AVFoundation;
using AVKit;
using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Transcription;
using ShowTime.Mac.Adapters;

namespace ShowTime.Mac.Video;

/// <summary>
/// A saved video, played in the window with macOS's own player (frame previews on hover over the timeline, full
/// screen, Space to play, ← and → to jump 5 seconds), with its subtitles beside it once there are any, and the file's
/// actions: generate the subtitles, show it in the Finder, record again, or send it to the Trash.
/// </summary>
internal sealed class VideoPreviewWindowController : NSWindowController
{
    private const int TranscriptWidth = 340;

    private readonly string _path;
    private readonly IShell _shell;
    private readonly IFileTrash _trash;
    private readonly Transcriptions _transcriptions;
    private readonly SubtitleEditor _subtitles;
    private readonly TranscriptPanelView _panel;
    private readonly NSSplitView _split;
    private readonly SeekingPlayerView _playerView;
    private readonly NSTextField _band;
    private readonly NSButton _generate;
    private readonly ILogger _log;
    private string? _microphoneTrack;
    private AVPlayer? _player;
    private NSObject? _timeObserver;
    private bool _durationKnown;
    private bool _transcriptShown;

    /// <param name="transcription">The transcription started with the recording, or null.</param>
    /// <param name="microphoneTrack">The microphone track kept for "Générer les sous-titres", or null.</param>
    /// <param name="log">Where the video's deletion and the window's closing are noted.</param>
    public VideoPreviewWindowController(string path, IShell shell, IFileTrash trash, SubtitleFile subtitleFile,
        Transcriptions transcriptions, TranscriptionJob? transcription, string? microphoneTrack, ILogger log) : base(CreateWindow(path))
    {
        _path = path;
        _log = log;
        _shell = shell;
        _trash = trash;
        _transcriptions = transcriptions;
        _microphoneTrack = microphoneTrack;
        var window = Window!;

        _subtitles = new SubtitleEditor(subtitleFile, transcriptions, TimeProvider.System, new MainThread());
        _subtitles.Load(path, transcription);
        _panel = new TranscriptPanelView(_subtitles);
        _panel.SeekRequested += Seek;
        _panel.EditingStarted += PauseIfPlaying;
        _panel.EditingEnded += () => Window?.MakeFirstResponder(_playerView);
        _subtitles.StatusChanged += UpdateGenerateButton;
        _subtitles.CueShown += ShowCue;

        _playerView = new SeekingPlayerView
        {
            ControlsStyle = AVPlayerViewControlsStyle.Floating,
            ShowsFullScreenToggleButton = true,
            Subtitles = _subtitles
        };
        _playerView.JumpRequested += direction => Seek(direction < 0
            ? PlayerSeek.Back(Position)
            : PlayerSeek.Forward(Position, Duration));
        _playerView.HomeRequested += () => Seek(TimeSpan.Zero);
        _playerView.LaidOut += PlaceBand;
        _band = NSTextField.CreateLabel("");
        _band.TextColor = NSColor.White;
        _band.DrawsBackground = true;
        _band.BackgroundColor = NSColor.FromSrgb(0f, 0f, 0f, 0.6f);
        _band.Alignment = NSTextAlignment.Center;
        _band.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        _band.MaximumNumberOfLines = 0;
        _band.Hidden = true;
        _playerView.ContentOverlayView?.AddSubview(_band);
        Load();

        var name = NSTextField.CreateLabel(path);
        name.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        name.TextColor = NSColor.SecondaryLabel;
        name.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        _generate = Button("Générer les sous-titres", "captions.bubble", "", Generate);
        var reveal = Button("Afficher dans le Finder", "folder", "Ouvrir le dossier avec la vidéo sélectionnée", Reveal);
        var retake = Button("Refaire", "arrow.counterclockwise", "Mettre cette vidéo à la Corbeille et en enregistrer une autre de la même façon",
            () => RetakeRequested?.Invoke(this, EventArgs.Empty));
        var delete = Button("Supprimer", "trash", "Envoyer la vidéo à la Corbeille", Delete);
        var bar = NSStackView.FromViews([name, _generate, reveal, retake, delete]);
        bar.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;

        // The subtitles on the left once there are any, the video on the right.
        _split = new NSSplitView { IsVertical = true, DividerStyle = NSSplitViewDividerStyle.Thin };
        _split.AddArrangedSubview(_playerView);

        var content = window.ContentView!;
        foreach (var view in new NSView[] { bar, _split })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            content.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            bar.TopAnchor.ConstraintEqualTo(content.TopAnchor, 8),
            bar.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 10),
            bar.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -10),
            _split.TopAnchor.ConstraintEqualTo(bar.BottomAnchor, 8),
            _split.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _split.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _split.BottomAnchor.ConstraintEqualTo(content.BottomAnchor)
        ]);
        if (_subtitles.HasSubtitles)
            ShowTranscript();
        UpdateGenerateButton();

        window.WillClose += (_, _) =>
        {
            // A subtitle still being typed is committed, and saved below.
            window.MakeFirstResponder(null);
            Release();
            _subtitles.Close(discard: false);
            // Never used: the subtitles were not asked for.
            if (_microphoneTrack is not null)
                _transcriptions.DiscardTrack(_microphoneTrack);
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Video preview of {Name} closed", Path.GetFileName(_path));
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>"Refaire": the caller sends the video to the Trash and records again the same way.</summary>
    public event EventHandler? RetakeRequested;

    public event EventHandler? Closed;

    private TimeSpan Position => _player is { } player && !double.IsNaN(player.CurrentTime.Seconds)
        ? TimeSpan.FromSeconds(player.CurrentTime.Seconds)
        : TimeSpan.Zero;

    /// <summary>The video's length, or zero while the player does not know it.</summary>
    private TimeSpan Duration => _player?.CurrentItem?.Duration is { IsIndefinite: false, IsInvalid: false } duration && !double.IsNaN(duration.Seconds)
        ? TimeSpan.FromSeconds(duration.Seconds)
        : TimeSpan.Zero;

    private static NSWindow CreateWindow(string path)
    {
        var window = new NSWindow(new CGRect(0, 0, 960, 600),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false)
        {
            Title = "ShowTime — " + Path.GetFileName(path),
            ContentMinSize = new CGSize(640, 420),
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua)
        };
        window.ReleaseWhenClosed(false);
        window.Center();
        return window;
    }

    public void Show()
    {
        AppActivation.BringToFront();
        ShowWindow(this);
        Window!.MakeKeyAndOrderFront(this);
        Window.MakeFirstResponder(_playerView);
        _player?.Play();
    }

    /// <summary>From the area selection on, a preview still playing would end up in the new video's sound.</summary>
    public void PauseIfPlaying() => _player?.Pause();

    /// <summary>Escape closes the window, as on Windows.</summary>
    [Export("cancelOperation:")]
    public void CancelOperation(NSObject? sender) => Window?.PerformClose(sender);

    /// <summary>
    /// Sends the video to the Trash, with its subtitles, and closes the window; its transcription stops. Returns false,
    /// with the video still shown, when it could not be moved.
    /// </summary>
    public bool Discard()
    {
        Release();
        try
        {
            if (File.Exists(_path))
                _trash.MoveToTrash(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Warning))
                _log.LogWarning(ex, "Video {Name} could not be sent to the Trash", Path.GetFileName(_path));
            MacDialogs.Show("La vidéo n'a pas pu être supprimée : " + ex.Message, NSAlertStyle.Warning);
            Load();
            return false;
        }
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Video {Name} sent to the Trash", Path.GetFileName(_path));
        _subtitles.Close(discard: true);
        TrashSubtitles();
        Window?.Close();
        return true;
    }

    private void TrashSubtitles()
    {
        var subtitles = SubtitleFile.PathFor(_path);
        try
        {
            if (File.Exists(subtitles))
                _trash.MoveToTrash(subtitles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The video is gone: its subtitles left behind are harmless.
        }
    }

    private void Delete()
    {
        _player?.Pause();
        if (MacDialogs.AskYesNo($"Envoyer « {Path.GetFileName(_path)} » à la Corbeille ?"))
            Discard();
    }

    private void Reveal()
    {
        if (File.Exists(_path))
            _shell.Reveal(_path);
        else
            MacDialogs.Show("La vidéo n'est plus à cet emplacement.", NSAlertStyle.Informational);
    }

    // ---- Subtitles -----------------------------------------------------------------------------------------

    private void UpdateGenerateButton()
    {
        _generate.Hidden = !_subtitles.CanGenerate;
        _generate.Enabled = _transcriptions.CanTranscribe;
        _generate.ToolTip = !_generate.Enabled ? "Modèle de transcription introuvable : voir les réglages."
            : _microphoneTrack is not null ? "Transcrire le micro de cette vidéo, sur cet ordinateur"
            : "Transcrire le son de cette vidéo, sur cet ordinateur";
    }

    /// <summary>The track goes to the transcription, which deletes it once done.</summary>
    private void Generate()
    {
        var track = _microphoneTrack;
        _microphoneTrack = null;
        _subtitles.Generate(track);
        ShowTranscript();
    }

    private void ShowTranscript()
    {
        if (_transcriptShown)
            return;
        _transcriptShown = true;
        _panel.WidthAnchor.ConstraintGreaterThanOrEqualTo(220).Active = true;
        _split.InsertArrangedSubview(_panel, 0);
        // The panel keeps its width when the window is resized: the video takes the rest, as on Windows.
        _split.SetHoldingPriority(260, 0);
        if (Window is { } window)
        {
            var frame = window.Frame;
            window.SetFrame(new CGRect(frame.X, frame.Y, frame.Width + TranscriptWidth, frame.Height), window.IsVisible);
            // Not shown yet (the subtitles came with the recording): it opens centred, wider.
            if (!window.IsVisible)
                window.Center();
            // The divider only moves once the split view has its size.
            window.ContentView?.LayoutSubtreeIfNeeded();
        }
        _split.SetPositionOfDivider(TranscriptWidth, 0);
    }

    private void ShowCue(string? text)
    {
        _band.StringValue = text ?? "";
        _band.Hidden = text is null;
        PlaceBand();
    }

    /// <summary>Puts the subtitle near the bottom of the picture, sized to it (<see cref="SubtitlePlacement"/>).</summary>
    private void PlaceBand()
    {
        if (_band.Hidden || _band.Superview is not { } overlay)
            return;
        var bounds = overlay.Bounds;
        var video = _player?.CurrentItem?.PresentationSize ?? CGSize.Empty;
        var placement = SubtitlePlacement.For(bounds.Width, bounds.Height, video.Width, video.Height);
        if (NSFont.SystemFontOfSize((nfloat)placement.FontSize) is { } font)
            _band.Font = font;
        var size = _band.SizeThatFits(new CGSize(placement.MaxWidth, bounds.Height));
        var width = Math.Min(placement.MaxWidth, (double)size.Width + 16);
        _band.Frame = new CGRect((bounds.Width - width) / 2, placement.BottomMargin, width, size.Height + 6);
    }

    // ---- Playback -------------------------------------------------------------------------------------------

    private void Seek(TimeSpan time)
    {
        if (_player is null)
            return;
        _player.Seek(CMTime.FromSeconds(time.TotalSeconds, 600), CMTime.Zero, CMTime.Zero);
        _subtitles.ShowPosition(time);
    }

    private void Load()
    {
        using var url = NSUrl.FromFilename(_path);
        _player = AVPlayer.FromUrl(url);
        _playerView.Player = _player;
        _durationKnown = false;
        // The highlighted subtitle follows the video closely; the length is known once the file is open.
        _timeObserver = _player.AddPeriodicTimeObserver(CMTime.FromSeconds(0.1, 600), DispatchQueue.MainQueue, time =>
        {
            if (!_durationKnown && Duration > TimeSpan.Zero)
            {
                _durationKnown = true;
                _subtitles.SetDuration(Duration);
                PlaceBand();
            }
            if (!double.IsNaN(time.Seconds))
                _subtitles.ShowPosition(TimeSpan.FromSeconds(time.Seconds));
        });
    }

    /// <summary>The player lets go of the file before it is moved.</summary>
    private void Release()
    {
        _player?.Pause();
        if (_timeObserver is not null)
        {
            _player?.RemoveTimeObserver(_timeObserver);
            _timeObserver = null;
        }
        _playerView.Player = null;
        _player?.Dispose();
        _player = null;
    }

    private static NSButton Button(string title, string symbol, string tip, Action action)
    {
        var button = NSButton.CreateButton(title, action);
        if (NSImage.GetSystemSymbol(symbol, null) is { } image)
        {
            button.Image = image;
            button.ImagePosition = NSCellImagePosition.ImageLeading;
        }
        button.ToolTip = tip;
        button.RefusesFirstResponder = true;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
            _playerView.Dispose();
        }
        base.Dispose(disposing);
    }
}
