using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ShowTime.Core.Files;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Core.Tests.Recording;

public sealed class RecordingSessionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ShowTimeSession");
    private static readonly string Out = Path.Combine(Root, "out");
    private static readonly string Ffmpeg = Path.Combine(Root, "bin", "ffmpeg.exe");
    private static readonly string Models = Path.Combine(Root, "models");
    private static readonly string Work = Path.Combine(Root, "work");
    private static readonly PixelRect VirtualScreen = new(0, 0, 1920, 1080);

    private readonly MockFileSystem _fs = new();
    private readonly AppSettings _settings = new(SettingsDefaults.ForWindows(Out)) { FileNamePattern = "Video_%NUM%", Counter = 8 };
    private readonly MemorySettingsStore _store = new();
    private readonly IAudioDeviceLister _lister = Substitute.For<IAudioDeviceLister>();
    private readonly IAreaPicker _picker = Substitute.For<IAreaPicker>();
    private readonly IScreen _screen = Substitute.For<IScreen>();
    private readonly FakeRecordingViewFactory _views = new();
    private readonly FakeHotkeys _hotkeys = new();
    private readonly FakeHighlightInput _highlightInput = new();
    private readonly FakeHighlightOverlayFactory _overlays = new();
    private readonly FakeRecorderFactory _recorders = new();
    private readonly IClipboard _clipboard = Substitute.For<IClipboard>();
    private readonly IUserDialogs _dialogs = Substitute.For<IUserDialogs>();
    private readonly FakeTimeProvider _clock = new();
    private readonly List<(string Path, bool FullScreen)> _saved = [];
    private readonly List<(string? Track, bool Transcribe)> _savedTracks = [];
    private readonly RecordingSession _session;
    private bool _hasAudioOutput = true;

    public RecordingSessionTests()
    {
        _fs.AddFile(Ffmpeg, new MockFileData(""));
        _settings.FfmpegPath = Ffmpeg;
        _screen.VirtualScreen.Returns(VirtualScreen);
        _lister.ListAsync(Arg.Any<string>()).Returns(["Mic A", "virtual-audio-capturer"]);
        _picker.PickAsync(false).Returns(new AreaSelection(new PixelRect(10, 20, 641, 481), new FakeImage()));

        _fs.AddFile(Path.Combine(Models, "ggml-small-q5_1.bin"), new MockFileData(""));
        var paths = Substitute.For<IAppPaths>();
        paths.WorkFolder.Returns(Work);
        var tracks = new MicrophoneTracks(_settings, new TranscriptionModelLocator(_fs, [Models]), paths, _fs);
        var counter = new FileCounter(_settings, _store);
        _session = new RecordingSession(
            _settings,
            new FfmpegLocator(_fs, Path.Combine(Root, "app"), null, "ffmpeg.exe", []),
            new DirectShowGdigrab(() => _hasAudioOutput), _lister, _picker, _screen, _views, _hotkeys,
            new Highlighter(_settings, _highlightInput, _overlays, new ImmediateUiThread(), _clock, HotkeyNaming.Windows), _recorders,
            new OutputPathAllocator(_fs, _clock), counter, _clipboard, _dialogs, new ImmediateUiThread(), _fs, tracks, _clock,
            HotkeyNaming.Windows);
        _session.VideoSaved += (_, e) =>
        {
            _saved.Add((e.Path, e.FullScreen));
            _savedTracks.Add((e.MicrophoneTrackPath, e.Transcribe));
        };
    }

    private FakeRecordingView View => _views.View!;
    private FakeRecorder Recorder => _recorders.Recorder!;

    private async Task ReadyAsync(bool fullScreen = false) => await _session.ToggleAsync(fullScreen, fromMenu: false);

    private async Task RecordingAsync()
    {
        await ReadyAsync();
        var start = _session.StartAsync();
        await _clock.RunUntil(Task.WhenAny(start, WaitFor(() => _recorders.Recorder is not null)));
        Recorder.CapturingSource.SetResult(true);
        await _clock.RunUntil(start);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(1);
    }

    [Fact]
    public async Task Without_the_shipped_ffmpeg_nothing_starts_and_the_user_is_told_to_reinstall()
    {
        _settings.FfmpegPath = "";
        await ReadyAsync();

        // Deliberate change: ffmpeg now ships with ShowTime, so the message no longer says how to install it.
        _dialogs.Received(1).ShowWarning(Arg.Is<string>(m =>
            m.StartsWith("ffmpeg est introuvable", StringComparison.Ordinal) && m.Contains("extrayez de nouveau tout le zip", StringComparison.Ordinal)));
        _session.IsBusy.ShouldBeFalse();
        await _picker.DidNotReceiveWithAnyArgs().PickAsync(default);
    }

    [Fact]
    public async Task A_configured_ffmpeg_that_is_gone_says_to_empty_the_setting()
    {
        var missing = Path.Combine(Root, "old", "ffmpeg.exe");
        _settings.FfmpegPath = missing;
        await ReadyAsync();

        _dialogs.Received(1).ShowWarning(Arg.Is<string>(m =>
            m.Contains(missing, StringComparison.Ordinal) && m.Contains("videz le champ", StringComparison.Ordinal) &&
            m.Contains("celui livré avec ShowTime", StringComparison.Ordinal) && !m.Contains("extrayez", StringComparison.Ordinal)));
        _session.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_selection_returns_to_idle()
    {
        _picker.PickAsync(false).Returns((AreaSelection?)null);
        await ReadyAsync();
        _session.IsBusy.ShouldBeFalse();
        _views.View.ShouldBeNull();
    }

    [Fact]
    public async Task After_the_selection_the_bar_waits_for_Start_with_even_dimensions_and_the_session_shortcuts()
    {
        await ReadyAsync();

        _session.IsReady.ShouldBeTrue();
        _session.IsBusy.ShouldBeTrue();
        _views.Area.ShouldBe(new PixelRect(10, 20, 640, 480));
        _views.FullScreen.ShouldBe(false);
        View.Calls.ShouldBe(["ready Ctrl+R Ctrl+D"]);
        View.Devices.ShouldBe(["Mic A", "virtual-audio-capturer"]);
        _hotkeys.Registered.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refused_session_shortcut_is_not_shown_on_the_bar()
    {
        Hotkey.TryParse("Ctrl+R", out var start).ShouldBeTrue();
        _hotkeys.Refused.Add(start);
        await ReadyAsync();
        View.Calls.ShouldBe(["ready  Ctrl+D"]);
    }

    [Fact]
    public async Task Full_screen_records_the_virtual_desktop_without_a_frame()
    {
        await ReadyAsync(fullScreen: true);
        _views.Area.ShouldBe(VirtualScreen);
        _views.FullScreen.ShouldBe(true);
        await _picker.DidNotReceiveWithAnyArgs().PickAsync(default);
    }

    [Fact]
    public async Task From_the_menu_the_selection_waits_for_the_menu_to_fade_out()
    {
        var toggle = _session.ToggleAsync(false, fromMenu: true);
        await _picker.DidNotReceiveWithAnyArgs().PickAsync(default);
        await _clock.RunUntil(toggle);
        _session.IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Start_counts_down_three_seconds_then_records_with_the_chosen_audio()
    {
        _settings.RecordSystemAudio = true;
        _settings.VideoFrameRate = 120;
        await RecordingAsync();

        // ffmpeg is launched during the countdown and captures at once here: no "starting" after 0.
        View.Calls.ShouldBe(["ready Ctrl+R Ctrl+D", "countdown 3", "countdown 3", "countdown 2", "countdown 1", "launching", "recording"]);
        // With a model, the microphone is also written alone, for subtitles now or from the player later.
        _recorders.Options.ShouldBe(new VideoOptions(60, "Mic A", true, _recorders.Options!.MicrophoneTrackPath));
        _recorders.Options.MicrophoneTrackPath.ShouldNotBeNull();
        _recorders.Area.ShouldBe(new PixelRect(10, 20, 640, 480));
        Recorder.OutputPath.ShouldBe(Path.Combine(Out, "Video_000008.mp4"));
        _settings.Counter.ShouldBe(9);
        _session.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public async Task Muted_audio_records_no_sound()
    {
        _settings.RecordMicrophone = false;
        _settings.RecordSystemAudio = false;
        await RecordingAsync();
        _recorders.Options.ShouldBe(new VideoOptions(30, null, false));
    }

    [Fact]
    public async Task System_audio_is_skipped_without_an_audio_output()
    {
        _hasAudioOutput = false;
        _settings.RecordSystemAudio = true;
        await RecordingAsync();
        _recorders.Options!.SystemAudio.ShouldBeFalse();
    }

    [Fact]
    public async Task System_audio_does_not_need_a_virtual_audio_driver()
    {
        _lister.ListAsync(Arg.Any<string>()).Returns(["Mic A"]);
        _settings.RecordSystemAudio = true;
        await RecordingAsync();
        _recorders.Options!.SystemAudio.ShouldBeTrue();
    }

    [Fact]
    public async Task The_counter_starts_anyway_when_ffmpeg_never_says_it_captures()
    {
        await ReadyAsync();
        var start = _session.StartAsync();
        await _clock.RunUntil(start);
        View.Calls[^1].ShouldBe("recording");
    }

    /// <summary>Starts the countdown and returns once ffmpeg is launched, with the time that took.</summary>
    private async Task<TimeSpan> LaunchedAfterAsync()
    {
        _recorders.Clock = _clock;
        var started = _clock.GetUtcNow();
        _ = _session.StartAsync();
        await _clock.RunUntil(WaitFor(() => _recorders.Recorder is not null));
        return _recorders.StartedAt!.Value - started;
    }

    [Fact]
    public async Task Ffmpeg_is_launched_before_the_end_of_the_countdown_so_that_it_captures_at_zero()
    {
        // It takes a while to open its inputs: "Démarrage…" after 0 was time the user believed recorded.
        await ReadyAsync();
        var launched = await LaunchedAfterAsync();

        launched.TotalSeconds.ShouldBe(2.3, 0.15);
        View.Calls.ShouldContain("countdown 1");
        View.Calls.ShouldNotContain("starting");
        _session.IsReady.ShouldBeFalse();
    }

    [Fact]
    public async Task The_view_is_told_before_ffmpeg_is_launched_so_that_the_bar_can_leave_the_picture()
    {
        // On macOS the bar cannot be kept out of a screen recording: covering the area, it must be gone first.
        var toldFirst = false;
        _recorders.Starting = () => toldFirst = View.Calls.Contains("launching");
        await ReadyAsync();
        await LaunchedAfterAsync();

        toldFirst.ShouldBeTrue();
        View.Calls.Count(c => c == "launching").ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_is_launched_nor_announced_when_Stop_comes_before_the_launch()
    {
        await ReadyAsync();
        var start = _session.StartAsync();
        await _session.StopAsync();
        await _clock.RunUntil(start);

        View.Calls.ShouldNotContain("launching");
    }

    [Fact]
    public async Task The_counter_starts_when_ffmpeg_captures_even_a_little_before_zero()
    {
        await ReadyAsync();
        await LaunchedAfterAsync();
        Recorder.CapturingSource.SetResult(true);
        await _clock.RunUntil(WaitFor(() => View.Calls[^1] == "recording"), maxSeconds: 1);

        View.Calls.ShouldNotContain("starting");
        _session.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public async Task Ffmpeg_is_launched_as_long_before_zero_as_it_took_last_time()
    {
        await ReadyAsync();
        await LaunchedAfterAsync();
        // 1.2 s to capture: past 0, so "starting" shows meanwhile.
        _clock.Advance(_recorders.StartedAt!.Value + TimeSpan.FromSeconds(1.2) - _clock.GetUtcNow());
        Recorder.CapturingSource.SetResult(true);
        await WaitFor(() => View.Calls[^1] == "recording");
        View.Calls.ShouldContain("starting");
        Recorder.StopResult.SetResult(true);
        await _clock.RunUntil(_session.StopAsync());

        await ReadyAsync();
        _recorders.Reset();
        (await LaunchedAfterAsync()).TotalSeconds.ShouldBe(1.8, 0.15);
    }

    [Fact]
    public async Task Stop_during_the_countdown_after_ffmpeg_was_launched_discards_it()
    {
        await ReadyAsync();
        await LaunchedAfterAsync();
        View.Calls.ShouldNotContain("starting");
        var recorder = Recorder;
        await _session.StopAsync();

        recorder.Killed.ShouldBeTrue();
        recorder.Disposed.ShouldBeTrue();
        _settings.Counter.ShouldBe(8);
        _session.IsReady.ShouldBeTrue();
        View.Calls[^1].ShouldBe("ready Ctrl+R Ctrl+D");
    }

    [Fact]
    public async Task The_start_shortcut_starts_the_countdown()
    {
        await ReadyAsync();
        _hotkeys.Press("Ctrl+R");
        View.Calls.ShouldContain("countdown 3");
        _session.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_during_the_countdown_goes_back_to_waiting_for_Start()
    {
        await ReadyAsync();
        var start = _session.StartAsync();
        await _session.StopAsync();
        await _clock.RunUntil(start);

        _session.IsReady.ShouldBeTrue();
        _recorders.Recorder.ShouldBeNull();
        View.Calls[^1].ShouldBe("ready Ctrl+R Ctrl+D");
    }

    [Fact]
    public async Task Nothing_is_highlighted_while_the_bar_waits_for_Start()
    {
        await ReadyAsync();
        _highlightInput.Registered.ShouldBeNull();
        View.HighlightShown.ShouldBe(0);
    }

    [Fact]
    public async Task Recording_enables_the_highlighting_over_the_area()
    {
        await RecordingAsync();
        _highlightInput.Registered.ShouldNotBeNull();
        View.HighlightShortcut.ShouldBe("Ctrl+Space");
        _overlays.Area.ShouldBe(new PixelRect(10, 20, 640, 480));
    }

    [Fact]
    public async Task Stopping_ends_the_highlighting()
    {
        await RecordingAsync();
        _ = _session.StopAsync();
        _highlightInput.Unregistrations.ShouldBe(1);
        _overlays.Overlay!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelling_ends_the_highlighting()
    {
        await RecordingAsync();
        View.ClickCancel();
        _highlightInput.Unregistrations.ShouldBe(1);
        _overlays.Overlay!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_during_the_countdown_never_enables_the_highlighting()
    {
        await ReadyAsync();
        var start = _session.StartAsync();
        await _session.StopAsync();
        await _clock.RunUntil(start);
        _highlightInput.Registered.ShouldBeNull();
        _overlays.Overlay.ShouldBeNull();
    }

    [Fact]
    public async Task An_invalid_trigger_does_not_prevent_recording()
    {
        _settings.HighlightTrigger = "Foo";
        await RecordingAsync();
        View.Calls.ShouldContain("recording");
        View.HighlightShown.ShouldBe(1);
        View.HighlightShortcut.ShouldBeNull();
    }

    [Fact]
    public async Task Stop_keeps_the_video_and_announces_it()
    {
        _settings.CopyPathAfterSave = true;
        await RecordingAsync();

        var stop = _session.StopAsync();
        _session.IsBusy.ShouldBeTrue();
        View.Calls[^1].ShouldBe("finishing");
        Recorder.StopResult.SetResult(true);
        await stop;

        _saved.ShouldBe([(Recorder.OutputPath, false)]);
        _clipboard.Received(1).SetText(Recorder.OutputPath);
        _session.IsBusy.ShouldBeFalse();
        View.Closed.ShouldBeTrue();
        Recorder.Disposed.ShouldBeTrue();
        _hotkeys.Registered.ShouldBeEmpty();
        _settings.Counter.ShouldBe(9);
    }

    [Fact]
    public async Task A_second_stop_waits_for_the_first()
    {
        await RecordingAsync();
        var first = _session.StopAsync();
        var second = _session.StopAsync();
        second.ShouldBeSameAs(first);
        Recorder.StopResult.SetResult(true);
        await second;
    }

    [Fact]
    public async Task A_video_that_could_not_be_finished_is_deleted_and_its_number_given_back()
    {
        await RecordingAsync();
        _fs.AddFile(Recorder.OutputPath, new MockFileData("partial"));
        Recorder.ErrorTail = "moov atom not found";

        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(false);
        await stop;

        _fs.File.Exists(Recorder.OutputPath).ShouldBeFalse();
        _settings.Counter.ShouldBe(8);
        _dialogs.Received(1).ShowError("La vidéo n'a pas pu être finalisée.\n\nmoov atom not found");
        _saved.ShouldBeEmpty();
        _session.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_during_the_recording_kills_ffmpeg_and_keeps_nothing()
    {
        await RecordingAsync();
        _fs.AddFile(Recorder.OutputPath, new MockFileData("partial"));

        _session.Cancel();

        Recorder.Killed.ShouldBeTrue();
        _fs.File.Exists(Recorder.OutputPath).ShouldBeFalse();
        _settings.Counter.ShouldBe(8);
        _session.IsBusy.ShouldBeFalse();
        View.Closed.ShouldBeTrue();
    }

    // ---- Microphone track for the transcription ----------------------------------------------------------

    /// <summary>The track ffmpeg was asked to write, created as ffmpeg would.</summary>
    private string RecordedTrack()
    {
        var track = _recorders.Options!.MicrophoneTrackPath!;
        _fs.AddFile(track, new MockFileData("RIFF"));
        return track;
    }

    [Fact]
    public async Task With_the_transcription_on_the_microphone_is_also_written_alone_and_given_with_the_video()
    {
        _settings.TranscribeMicrophone = true;
        await RecordingAsync();
        var track = RecordedTrack();
        Path.GetDirectoryName(track).ShouldBe(Work);

        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(true);
        await stop;

        _savedTracks.ShouldBe([(track, true)]);
        _fs.File.Exists(track).ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_automatic_transcription_the_track_is_still_kept_for_the_player()
    {
        await RecordingAsync();
        var track = RecordedTrack();

        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(true);
        await stop;

        _savedTracks.ShouldBe([(track, false)]);
    }

    [Fact]
    public async Task The_choice_to_transcribe_is_the_one_made_when_the_recording_started()
    {
        _settings.TranscribeMicrophone = true;
        await RecordingAsync();
        RecordedTrack();
        _settings.TranscribeMicrophone = false;

        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(true);
        await stop;

        _savedTracks.ShouldHaveSingleItem().Transcribe.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_microphone_there_is_nothing_to_transcribe()
    {
        _settings.TranscribeMicrophone = true;
        _settings.RecordMicrophone = false;

        await RecordingAsync();

        _recorders.Options!.MicrophoneTrackPath.ShouldBeNull();
    }

    [Fact]
    public async Task Cancel_deletes_the_track_with_the_video()
    {
        _settings.TranscribeMicrophone = true;
        await RecordingAsync();
        var track = RecordedTrack();

        _session.Cancel();

        _fs.File.Exists(track).ShouldBeFalse();
    }

    [Fact]
    public async Task A_video_that_could_not_be_finished_takes_its_track_with_it()
    {
        _settings.TranscribeMicrophone = true;
        await RecordingAsync();
        var track = RecordedTrack();

        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(false);
        await stop;

        _fs.File.Exists(track).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_from_the_bar_while_ready_closes_it()
    {
        await ReadyAsync();
        View.ClickCancel();
        _session.IsBusy.ShouldBeFalse();
        View.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unexpected_ffmpeg_exit_is_explained_with_its_log_and_an_audio_hint()
    {
        await RecordingAsync();
        Recorder.ErrorTail = "Could not run graph";

        Recorder.OnUnexpectedExit!(Recorder);

        _dialogs.Received(1).ShowError(Arg.Is<string>(m =>
            m.StartsWith("L'enregistrement s'est arrêté :\n\nCould not run graph", StringComparison.Ordinal) &&
            m.Contains("coupez le micro", StringComparison.Ordinal)));
        _session.IsBusy.ShouldBeFalse();
        _settings.Counter.ShouldBe(8);
    }

    [Fact]
    public async Task An_exit_reported_for_an_older_recorder_is_ignored()
    {
        await RecordingAsync();
        var old = new FakeRecorder("old.mp4");
        Recorder.OnUnexpectedExit!(old);
        _session.IsRecording.ShouldBeTrue();
    }

    [Fact]
    public async Task A_recorder_that_fails_to_start_is_reported_and_the_number_given_back()
    {
        _recorders.StartFailure = new InvalidOperationException("access denied");
        await ReadyAsync();
        await _clock.RunUntil(_session.StartAsync());

        // Not only ffmpeg: capturing the computer sound can fail too.
        _dialogs.Received(1).ShowError("L'enregistrement n'a pas pu démarrer : access denied");
        _settings.Counter.ShouldBe(8);
        _session.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Toggle_while_ready_starts_and_while_recording_stops()
    {
        await RecordingAsync();
        var toggle = _session.ToggleAsync(false, false);
        Recorder.StopResult.SetResult(true);
        await toggle;
        _saved.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_stop_that_throws_is_reported_and_ShowTime_accepts_new_captures()
    {
        await RecordingAsync();
        var stop = _session.StopAsync();
        Recorder.StopResult.SetException(new IOException("pipe closed"));
        await stop;

        _dialogs.Received(1).ShowError("La vidéo n'a pas pu être finalisée.\n\npipe closed");
        _session.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task A_busy_clipboard_does_not_lose_the_saved_video()
    {
        _settings.CopyPathAfterSave = true;
        _clipboard.When(c => c.SetText(Arg.Any<string>())).Do(_ => throw new InvalidOperationException("busy"));
        await RecordingAsync();
        var stop = _session.StopAsync();
        Recorder.StopResult.SetResult(true);
        await stop;
        _saved.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_during_a_recording_kills_ffmpeg_and_releases_the_shortcuts()
    {
        await RecordingAsync();
        _session.Dispose();
        Recorder.Killed.ShouldBeTrue();
        _hotkeys.Registered.ShouldBeEmpty();
        View.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_when_idle_does_nothing()
    {
        var changes = 0;
        _session.StateChanged += (_, _) => changes++;
        _session.Cancel();
        await _session.StopAsync();
        changes.ShouldBe(0);
    }

    [Fact]
    public async Task Toggle_during_the_selection_is_ignored()
    {
        var pending = new TaskCompletionSource<AreaSelection?>();
        _picker.PickAsync(false).Returns(pending.Task);
        var first = _session.ToggleAsync(false, false);
        await _session.ToggleAsync(false, false);
        pending.SetResult(null);
        await first;
        await _picker.Received(1).PickAsync(false);
    }

    [Fact]
    public async Task The_stop_shortcut_stops_the_recording()
    {
        await RecordingAsync();
        _hotkeys.Press("Ctrl+D");
        Recorder.StopRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task StateChanged_follows_each_step()
    {
        var changes = 0;
        _session.StateChanged += (_, _) => changes++;
        await ReadyAsync();
        changes.ShouldBe(2);
    }
}
