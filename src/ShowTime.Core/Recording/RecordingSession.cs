using System.IO.Abstractions;
using ShowTime.Core.Capture;
using ShowTime.Core.Files;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Core.Recording;

/// <summary>
/// One screen recording at a time: area selection, then a bar to choose the audio and start, a 3-second countdown,
/// ffmpeg, and the MP4 in the output folder. Every member is called on the UI thread.
/// </summary>
/// <remarks>
/// After the selection, the bar waits in Ready: the countdown only starts from its Start button or the
/// Start shortcut. The Start and Stop shortcuts are registered from Ready until the session ends, so that they do not
/// steal those keys from other applications the rest of the time; the highlight trigger only while recording.
/// </remarks>
public sealed class RecordingSession(
    AppSettings settings, FfmpegLocator locator, IFfmpegPlatform platform, IAudioDeviceLister audioDevices,
    IAreaPicker picker, IScreen screen, IRecordingViewFactory views, IGlobalHotkeys sessionHotkeys,
    Highlighter highlighter, IVideoRecorderFactory recorders, OutputPathAllocator paths, FileCounter counter, IClipboard clipboard,
    IUserDialogs dialogs, IUiThread uiThread, IFileSystem fileSystem, MicrophoneTracks microphoneTracks, TimeProvider clock,
    HotkeyNaming hotkeyNaming)
    : IRecordingStatus, IDisposable
{
    private enum State { Idle, Selecting, Ready, Countdown, Recording, Finishing }

    private const int CountdownSeconds = 3;

    /// <summary>How long ffmpeg takes to capture before it has done it once: the time to open its devices.</summary>
    private static readonly TimeSpan DefaultStartupTime = TimeSpan.FromSeconds(0.7);

    /// <summary>Should ffmpeg never announce that it captures, the counter starts anyway after this.</summary>
    private static readonly TimeSpan CaptureStartTimeout = TimeSpan.FromSeconds(5);

    private State _state = State.Idle;
    private IRecordingView? _view;
    private IVideoRecorder? _recorder;

    /// <summary>The current recorder's microphone track, for the transcription; null without one.</summary>
    private string? _microphoneTrack;

    /// <summary>The bar's subtitles toggle when the recording started: it is locked from then on.</summary>
    private bool _transcribe;
    private Task _stopping = Task.CompletedTask;

    /// <summary>How long ffmpeg last took from its launch to capturing; it is launched that long before 0.</summary>
    private TimeSpan _startupTime = DefaultStartupTime;

    private string _ffmpeg = "";
    private PixelRect _target;
    private bool _fullScreen;
    private Task<IReadOnlyList<string>> _devices = Task.FromResult<IReadOnlyList<string>>([]);
    private string? _startShortcut;
    private string? _stopShortcut;

    /// <summary>Incremented when a countdown is abandoned, so that its pending loop does not start the recording.</summary>
    private int _session;

    /// <summary>The %NUM% value taken by the current video, given back if nothing is kept.</summary>
    private int? _reservedNumber;

    public event EventHandler? StateChanged;

    public event EventHandler<VideoSavedEventArgs>? VideoSaved;

    public bool IsBusy => _state != State.Idle;

    /// <summary>The area is chosen and the bar waits for Start.</summary>
    public bool IsReady => _state == State.Ready;

    public bool IsRecording => _state is State.Countdown or State.Recording;

    /// <summary>The video shortcut chooses the area, then starts the countdown, then stops the recording.</summary>
    public async Task ToggleAsync(bool fullScreen, bool fromMenu)
    {
        switch (_state)
        {
            case State.Ready:
                await StartAsync();
                return;
            case State.Countdown or State.Recording:
                await StopAsync();
                return;
            case State.Selecting or State.Finishing:
                return;
        }

        if (locator.Find(settings.FfmpegPath) is not { } ffmpeg)
        {
            // A configured ffmpeg that has gone (moved by an upgrade) is fixed in the settings, not by reinstalling.
            dialogs.ShowWarning(string.IsNullOrWhiteSpace(settings.FfmpegPath)
                ? "ffmpeg est introuvable : ShowTime s'en sert pour encoder les vidéos.\n\n" + platform.InstallHint
                : $"Le ffmpeg indiqué dans les réglages est introuvable : {settings.FfmpegPath}\n\n" +
                  "Ouvrez les réglages et videz le champ ffmpeg pour utiliser celui livré avec ShowTime.");
            return;
        }

        SetState(State.Selecting);
        PixelRect? selected = null;
        try
        {
            if (fromMenu)
                await Task.Delay(CaptureWorkflow.MenuFadeOut, clock);
            if (fullScreen)
            {
                selected = screen.VirtualScreen;
            }
            else if (await picker.PickAsync(windowMode: false) is { } selection)
            {
                selection.Image.Dispose();
                selected = selection.Area;
            }
        }
        finally
        {
            if (selected is null)
                SetState(State.Idle);
        }
        if (selected is not { } chosen)
            return;

        _target = chosen.WithEvenSize();
        _fullScreen = fullScreen;
        _ffmpeg = ffmpeg;
        // Listed while the user looks at the bar: the toggles then show what this computer can record.
        _devices = audioDevices.ListAsync(ffmpeg);

        var view = _view = views.Show(_target, fullScreen);
        view.StartRequested += (_, _) => _ = StartAsync();
        view.StopRequested += (_, _) => _ = StopAsync();
        view.CancelRequested += (_, _) => Cancel();
        RegisterSessionHotkeys();
        view.ShowReady(_startShortcut, _stopShortcut);
        SetState(State.Ready);

        var devices = await _devices;
        if (_view == view)
            view.SetAudioDevices([.. devices]);
    }

    /// <summary>
    /// Start button or shortcut: 3, 2, 1, then the recording. ffmpeg takes a while to open its inputs, so it is launched
    /// that long before the end of the countdown, and the counter starts when it actually captures: at 0, what is said
    /// is recorded.
    /// </summary>
    public async Task StartAsync()
    {
        if (_state != State.Ready || _view is not { } view)
            return;
        var session = ++_session;
        SetState(State.Countdown);
        view.ShowCountdown(CountdownSeconds);

        // Usually listed long ago; at worst ffmpeg's own timeout applies.
        var devices = await _devices;
        if (session != _session || _state != State.Countdown)
            return;
        var countdown = TimeSpan.FromSeconds(CountdownSeconds);
        var launching = LaunchAsync(countdown - (_startupTime < countdown ? _startupTime : countdown), session, devices, view);
        for (var seconds = CountdownSeconds; seconds > 0; seconds--)
        {
            if (session != _session || _state != State.Countdown)
                return;
            view.ShowCountdown(seconds);
            await Task.Delay(TimeSpan.FromSeconds(1), clock);
        }
        if (await launching is not { } started || session != _session || _state != State.Countdown)
            return;

        // Slower than last time: Stop already works, and the counter waits for the devices, so that the first words
        // are not lost. Should ffmpeg never announce it, the counter starts anyway.
        view.ShowStarting();
        SetState(State.Recording);
        await Task.WhenAny(started.Capturing, Task.Delay(CaptureStartTimeout, clock));
        if (_recorder == started && _view == view && _state == State.Recording)
            view.ShowRecording();
    }

    /// <summary>Launches ffmpeg after the delay, unless the countdown was abandoned; null when nothing was launched.</summary>
    private async Task<IVideoRecorder?> LaunchAsync(TimeSpan delay, int session, IReadOnlyList<string> devices, IRecordingView view)
    {
        await Task.Delay(delay, clock);
        if (session != _session || _state != State.Countdown)
            return null;

        IVideoRecorder started;
        try
        {
            var microphone = settings.RecordMicrophone
                ? AudioDevices.PickMicrophone(devices, settings.Microphone, platform.SystemAudioDevice)
                : null;
            var systemAudio = settings.RecordSystemAudio && platform.CanRecordSystemAudio(devices);
            var track = microphone is not null ? microphoneTracks.Allocate() : null;
            var options = new VideoOptions(Math.Clamp(settings.VideoFrameRate, 5, 60), microphone, systemAudio, track);
            var path = paths.Next(settings.OutputFolder, settings.FileNamePattern, settings.Counter, ".mp4");
            // Taken now, so that an image saved meanwhile gets the next number instead of the same one.
            _reservedNumber = counter.Reserve();
            _microphoneTrack = track;
            _transcribe = settings.TranscribeMicrophone;
            // Before the launch: ffmpeg's first frames come before 0, and a bar on the area must not be in them.
            view.ShowLaunching();
            _recorder = started = recorders.Start(_ffmpeg, _target, path, options,
                recorder => uiThread.Post(() => OnUnexpectedExit(recorder)));
        }
#pragma warning disable CA1031 // Any failure to launch ffmpeg ends the session with a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReleaseNumber();
            DeleteMicrophoneTrack();
            CloseUi();
            SetState(State.Idle);
            dialogs.ShowError("L'enregistrement n'a pas pu démarrer : " + ex.Message);
            return null;
        }
        _ = StartCounterOnCaptureAsync(started, clock.GetTimestamp(), session, view);
        return started;
    }

    /// <summary>Ends the countdown as soon as ffmpeg captures, and remembers how long that took for the next one.</summary>
    private async Task StartCounterOnCaptureAsync(IVideoRecorder started, long launchedAt, int session, IRecordingView view)
    {
        if (!await started.Capturing)
            return;
        _startupTime = clock.GetElapsedTime(launchedAt);
        if (_recorder != started || _view != view || session != _session || _state != State.Countdown)
            return;
        SetState(State.Recording);
        view.ShowRecording();
    }

    /// <summary>
    /// Ends the recording and keeps the video. During the countdown nothing is recorded yet: the bar goes back to
    /// waiting for Start. While a stop is already in progress, returns that one.
    /// </summary>
    public Task StopAsync()
    {
        switch (_state)
        {
            case State.Countdown when _view is { } view:
                _session++;
                // ffmpeg may already be launched: what it captured before 0 is not kept.
                DiscardRecorder();
                view.ShowReady(_startShortcut, _stopShortcut);
                SetState(State.Ready);
                return Task.CompletedTask;
            case State.Finishing:
                return _stopping;
            case State.Recording when _recorder is { } recorder:
                _stopping = FinishAsync(recorder);
                return _stopping;
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>Abandons the prepared recording, the countdown or the recording; nothing is kept.</summary>
    public void Cancel()
    {
        if (_state is not (State.Ready or State.Countdown or State.Recording))
            return;
        _session++;
        DiscardRecorder();
        CloseUi();
        SetState(State.Idle);
    }

    public void Dispose()
    {
        DiscardRecorder();
        CloseUi();
        sessionHotkeys.Dispose();
        highlighter.Dispose();
    }

    /// <summary>Keeps the shortcuts the system accepted, to show them on the bar.</summary>
    private void RegisterSessionHotkeys()
    {
        string? Bind(string text, Action action) =>
            Hotkey.TryParse(text, out var hotkey) && sessionHotkeys.Register(hotkey, action) ? hotkey.Format(hotkeyNaming) : null;

        _startShortcut = Bind(settings.HotkeyVideoStart, () =>
        {
            if (_state == State.Ready)
                _ = StartAsync();
        });
        _stopShortcut = Bind(settings.HotkeyVideoStop, () =>
        {
            if (_state is State.Countdown or State.Recording)
                _ = StopAsync();
        });
    }

    private async Task FinishAsync(IVideoRecorder recorder)
    {
        SetState(State.Finishing);
        _view?.ShowFinishing();
        var track = _microphoneTrack;
        _microphoneTrack = null;

        var ok = false;
        string? error = null;
        try
        {
            ok = await recorder.StopAsync();
        }
#pragma warning disable CA1031 // Reported below; ShowTime must accept new captures afterwards.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex.Message;
        }
        finally
        {
            // Whatever happens, ShowTime must accept new captures afterwards.
            _recorder = null;
            recorder.Dispose();
            CloseUi();
            SetState(State.Idle);
        }

        if (!ok)
        {
            ReleaseNumber();
            TryDelete(recorder.OutputPath);
            if (track is not null)
                TryDelete(track);
            var reason = error ?? (recorder.ErrorTail.Length > 0 ? recorder.ErrorTail : "ffmpeg n'a pas terminé le fichier.");
            dialogs.ShowError("La vidéo n'a pas pu être finalisée.\n\n" + reason);
            return;
        }

        _reservedNumber = null;
        if (settings.CopyPathAfterSave)
        {
            try
            {
                clipboard.SetText(recorder.OutputPath);
            }
#pragma warning disable CA1031 // The video is saved; a busy clipboard is not worth an error.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
        VideoSaved?.Invoke(this, new VideoSavedEventArgs(recorder.OutputPath, _fullScreen, track, _transcribe));
    }

    private void OnUnexpectedExit(IVideoRecorder recorder)
    {
        if (_recorder != recorder)
            return;
        var log = recorder.ErrorTail;
        var withAudio = settings.RecordMicrophone || settings.RecordSystemAudio;
        DiscardRecorder();
        CloseUi();
        SetState(State.Idle);

        var hint = withAudio
            ? "\n\nSi le message parle d'un périphérique audio, coupez le micro ou le son de l'ordinateur sur la barre " +
              "d'enregistrement, ou choisissez un autre micro dans les réglages."
            : "";
        dialogs.ShowError("L'enregistrement s'est arrêté :\n\n" + (log.Length > 0 ? log : "ffmpeg s'est arrêté sans explication.") + hint);
    }

    /// <summary>Killing ffmpeg leaves an unplayable file: it is deleted.</summary>
    private void DiscardRecorder()
    {
        if (_recorder is not { } recorder)
            return;
        _recorder = null;
        recorder.Kill();
        TryDelete(recorder.OutputPath);
        recorder.Dispose();
        ReleaseNumber();
        DeleteMicrophoneTrack();
    }

    private void DeleteMicrophoneTrack()
    {
        if (_microphoneTrack is { } track)
            TryDelete(track);
        _microphoneTrack = null;
    }

    private void ReleaseNumber()
    {
        counter.Release(_reservedNumber);
        _reservedNumber = null;
    }

    private void CloseUi()
    {
        sessionHotkeys.UnregisterAll();
        _view?.Dispose();
        _view = null;
    }

    private void SetState(State state)
    {
        var previous = _state;
        _state = state;
        if (state == State.Recording && previous != State.Recording)
            _view?.ShowHighlightShortcut(highlighter.Enable(_target));
        else if (previous == State.Recording && state != State.Recording)
            highlighter.Disable();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryDelete(string path)
    {
        try
        {
            fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An incomplete file left behind is harmless.
        }
    }
}
