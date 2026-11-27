using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Mac.Recording;

/// <summary>The recording bar and, for an area, the red frame around it.</summary>
internal sealed class MacRecordingView : IRecordingView
{
    private readonly RecordingBar _bar;
    private RecordingFrame? _frame;

    public MacRecordingView(AppSettings settings, ISettingsStore store, IFfmpegPlatform platform, INativeWindows windows, bool canTranscribe,
        PixelRect area, bool fullScreen)
    {
        if (!fullScreen)
            _frame = new RecordingFrame(area, windows);
        _bar = new RecordingBar(settings, store, platform, windows, canTranscribe);
        _bar.StartRequested += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        _bar.StopRequested += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        _bar.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _bar.ShowNear(area);
    }

    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? CancelRequested;

    public void ShowReady(string? startShortcut, string? stopShortcut) => _bar.ShowReady(startShortcut, stopShortcut);
    public void SetAudioDevices(IReadOnlyCollection<string> devices) => _bar.SetAudioDevices(devices);
    public void ShowCountdown(int seconds) => _bar.ShowCountdown(seconds);
    public void ShowLaunching() => _bar.LeaveThePicture();
    public void ShowStarting() => _bar.ShowStarting();
    public void ShowRecording() => _bar.ShowRecording();

    public void ShowHighlightShortcut(string? shortcut) => _bar.ShowHighlightShortcut(shortcut);

    /// <summary>Nothing more is recorded: the frame goes at once.</summary>
    public void ShowFinishing()
    {
        _bar.ShowFinishing();
        CloseFrame();
    }

    public void Dispose()
    {
        CloseFrame();
        _bar.CloseBar();
        // Released once the click that may have ended the session (Cancel, Stop) has returned.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(_bar.Dispose);
    }

    private void CloseFrame()
    {
        _frame?.Dispose();
        _frame = null;
    }
}

internal sealed class MacRecordingViewFactory(
    AppSettings settings, ISettingsStore store, IFfmpegPlatform platform, INativeWindows windows, MicrophoneTracks tracks) : IRecordingViewFactory
{
    public IRecordingView Show(PixelRect area, bool fullScreen) =>
        new MacRecordingView(settings, store, platform, windows, tracks.CanTranscribe, area, fullScreen);
}
