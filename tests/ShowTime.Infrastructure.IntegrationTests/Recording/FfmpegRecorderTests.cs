using System.ComponentModel;
using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Pipes;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Infrastructure.Platform;
using ShowTime.Infrastructure.Recording;

namespace ShowTime.Infrastructure.IntegrationTests.Recording;

/// <summary>
/// Records with ffmpeg's own test sources (a pattern and a tone) instead of the screen, with the production encoding.
/// Skipped when ffmpeg is not installed.
/// </summary>
public sealed class FfmpegRecorderTests : IDisposable
{
    private static readonly string? Ffmpeg = AppPaths.CreateFfmpegLocator(new FileSystem()).Find("");

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string RequireFfmpeg()
    {
        Assert.SkipWhen(Ffmpeg is null, "ffmpeg is not installed.");
        return Ffmpeg!;
    }

    private static CaptureInputs TestSources(bool audio) => new(
        audio ? [["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]] : [],
        ["-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30"]);

    [Fact]
    public async Task Stop_writes_a_playable_MP4_with_its_video_and_audio()
    {
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("video.mp4");
        var unexpected = false;

        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, TestSources(audio: true), output, _ => unexpected = true);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();

        unexpected.ShouldBeFalse();
        var probe = await RunFfmpegAsync(ffmpeg, "-hide_banner", "-i", output, "-f", "null", "-");
        probe.ExitCode.ShouldBe(0);
        probe.Output.ShouldContain("Video: h264");
        probe.Output.ShouldContain("Audio: aac");
    }

    [Fact]
    public async Task Stop_keeps_the_end_of_the_sound()
    {
        // "q" used to discard about 0.35 s of audio held behind the video frames (see CLAUDE.md).
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("ends.mp4");
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, TestSources(audio: true), output, _ => { });
        await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();

        var video = await StreamDurationAsync(ffmpeg, output, "v:0");
        var audio = await StreamDurationAsync(ffmpeg, output, "a:0");
        Math.Abs(video - audio).ShouldBeLessThan(0.25);
    }

    [Fact]
    public async Task The_microphone_track_is_a_16_kHz_mono_wav_in_time_with_the_video()
    {
        // The transcription's times are read from this track and shown against the video: a beep every second must
        // start at the same time in both files.
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("track.mp4");
        var track = _folder.File("track.wav");
        var inputs = new CaptureInputs(
            [["-re", "-f", "lavfi", "-i", "aevalsrc=if(lt(mod(t\\,1)\\,0.2)\\,0.5*sin(2*PI*880*t)\\,0):s=48000"]],
            ["-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30"]);

        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, inputs, output, _ => { }, microphoneTrackPath: track);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await Task.Delay(3500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();

        var probe = await RunFfmpegAsync(ffmpeg, "-hide_banner", "-i", track);
        probe.Output.ShouldContain("Audio: pcm_s16le");
        // ffmpeg 5 writes "mono", 6.1 "1 channels".
        probe.Output.ShouldMatch(@"16000 Hz, (mono|1 channels)");
        var inTrack = await BeepStartsAsync(ffmpeg, track);
        var inVideo = await BeepStartsAsync(ffmpeg, output);
        inTrack.Count.ShouldBeGreaterThanOrEqualTo(2);
        for (var i = 0; i < Math.Min(inTrack.Count, inVideo.Count); i++)
            Math.Abs(inTrack[i] - inVideo[i]).ShouldBeLessThan(0.05, $"beep {i}: {inTrack[i]} s in the track, {inVideo[i]} s in the video");
    }

    /// <summary>Where each beep starts, in seconds of the file's timeline: the ends of the silences between them.</summary>
    private static async Task<List<double>> BeepStartsAsync(string ffmpeg, string file)
    {
        var (_, output) = await RunFfmpegAsync(ffmpeg, "-hide_banner", "-i", file, "-map", "0:a:0", "-af",
            "silencedetect=noise=-30dB:d=0.3", "-f", "null", "-");
        return [.. System.Text.RegularExpressions.Regex.Matches(output, @"silence_end: ([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
    }

    [Fact]
    public async Task Kill_stops_at_once()
    {
        var ffmpeg = RequireFfmpeg();
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, TestSources(audio: false), _folder.File("killed.mp4"), _ => { });
        await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        recorder.Kill();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_ffmpeg_that_fails_on_its_own_is_reported_with_its_errors()
    {
        var ffmpeg = RequireFfmpeg();
        var exited = new TaskCompletionSource<IVideoRecorder>();
        var broken = new CaptureInputs([], ["-f", "lavfi", "-i", "nosuchsource"]);

        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, broken, _folder.File("broken.mp4"), r => exited.TrySetResult(r));

        (await exited.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeSameAs(recorder);
        (await recorder.Capturing).ShouldBeFalse();
        recorder.ErrorTail.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_factory_records_the_platform_inputs()
    {
        var ffmpeg = RequireFfmpeg();
        var platform = new TestSourcePlatform();
        var factory = new FfmpegVideoRecorderFactory(platform);
        using var recorder = factory.Start(ffmpeg, new PixelRect(0, 0, 320, 240), _folder.File("factory.mp4"), new VideoOptions(30, null, false), _ => { });
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        // A few frames first: a video stopped before its first frame is a failure.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();
        platform.Area.ShouldBe(new PixelRect(0, 0, 320, 240));
    }

    private static CaptureInputs WithPipe(TestPipe pipe) =>
        new([], ["-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30"], TrailingAudioInputs: [pipe.Input], DownmixToStereo: true);

    [Fact]
    public async Task Piped_raw_audio_is_recorded()
    {
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("piped.mp4");
        var pipe = new TestPipe(feedFor: null);
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, WithPipe(pipe), output, _ => { }, pipe);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();

        pipe.Disposed.ShouldBeTrue();
        var probe = await RunFfmpegAsync(ffmpeg, "-hide_banner", "-i", output, "-f", "null", "-");
        probe.Output.ShouldContain("Audio: aac");
    }

    [Fact]
    public async Task Stop_finishes_the_file_when_the_computer_sound_stops_coming()
    {
        // On "q", ffmpeg waits for its input threads, and the one reading an empty pipe never returns: the recorder
        // closes the pipe, which ends that input.
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("starved.mp4");
        var pipe = new TestPipe(TimeSpan.FromSeconds(1));
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, WithPipe(pipe), output, _ => { }, pipe);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        await Task.Delay(2500, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        (await recorder.StopAsync()).ShouldBeTrue();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        var probe = await RunFfmpegAsync(ffmpeg, "-hide_banner", "-i", output, "-f", "null", "-");
        probe.Output.ShouldContain("Video: h264");
    }

    [Theory]
    [InlineData("[warning] Finishing stream 0:0 without any data written to it.", true)]
    [InlineData("[vost#0:0/libx264 @ 000001c2] [warning] Finishing stream without any data written to it.", true)]
    [InlineData("[warning] Finishing stream 0:1 without any data written to it.", false)]
    [InlineData("[aost#0:1/aac @ 000001c2] [warning] Finishing stream without any data written to it.", false)]
    public void An_empty_video_is_recognised_in_old_and_new_ffmpeg_wordings(string line, bool noVideo) =>
        FfmpegVideoRecorder.SaysNoVideoWasWritten(line).ShouldBe(noVideo);

    [Fact]
    public async Task A_video_is_saved_only_when_it_has_frames()
    {
        // A pipe silent from the start: ffmpeg 5 holds every frame waiting for the sound, and closing the pipe gets an
        // MP4 without a single frame, with exit code 0. That must not count as a saved video. Whatever this ffmpeg does
        // with it, Stop must say saved exactly when the file has frames.
        var ffmpeg = RequireFfmpeg();
        var output = _folder.File("empty.mp4");
        var pipe = new TestPipe(TimeSpan.Zero);
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, WithPipe(pipe), output, _ => { }, pipe);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        var saved = await recorder.StopAsync();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        var (_, frames) = await RunFfmpegAsync(ffmpeg, "-v", "error", "-i", output, "-map", "0:v:0?", "-c", "copy", "-f", "framecrc", "-");
        var hasFrames = frames.Split('\n').Any(l => l.StartsWith("0,", StringComparison.Ordinal));
        saved.ShouldBe(hasFrames, "ffmpeg said:\n" + recorder.ErrorTail);
    }

    [Fact]
    public async Task The_computer_sound_is_freed_only_once_ffmpeg_has_finished()
    {
        var ffmpeg = RequireFfmpeg();
        var capture = new FakeCapture();
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, TestSources(audio: true), _folder.File("freed.mp4"), _ => { }, capture);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        capture.Disposed.ShouldBeFalse();
        // A few frames first: a video stopped before its first frame is a failure.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();
        capture.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Kill_frees_the_computer_sound()
    {
        var ffmpeg = RequireFfmpeg();
        var capture = new FakeCapture();
        using var recorder = FfmpegVideoRecorder.Start(ffmpeg, TestSources(audio: false), _folder.File("killed2.mp4"), _ => { }, capture);
        await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        recorder.Kill();
        capture.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_factory_pipes_the_computer_sound_of_its_source()
    {
        var ffmpeg = RequireFfmpeg();
        var platform = new TestSourcePlatform();
        var source = new FakeSource();
        var factory = new FfmpegVideoRecorderFactory(platform, source);
        using var recorder = factory.Start(ffmpeg, new PixelRect(0, 0, 320, 240), _folder.File("sound.mp4"), new VideoOptions(30, null, true), _ => { });
        platform.SystemAudio.ShouldBeSameAs(source.Capture!.Input);
        (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
        // A few frames first: a video stopped before its first frame is a failure.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await recorder.StopAsync()).ShouldBeTrue();
        source.Capture.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Without_the_computer_sound_the_factory_captures_nothing()
    {
        var source = new FakeSource();
        var factory = new FfmpegVideoRecorderFactory(new TestSourcePlatform(), source);
        Should.Throw<Win32Exception>(() => factory.Start(_folder.File("not-ffmpeg.exe"), new PixelRect(0, 0, 320, 240),
            _folder.File("x.mp4"), new VideoOptions(30, null, false), _ => { }));
        source.Capture.ShouldBeNull();
    }

    [Fact]
    public void A_failed_launch_frees_the_computer_sound()
    {
        var source = new FakeSource();
        var factory = new FfmpegVideoRecorderFactory(new TestSourcePlatform(), source);
        Should.Throw<Win32Exception>(() => factory.Start(_folder.File("not-ffmpeg.exe"), new PixelRect(0, 0, 320, 240),
            _folder.File("x.mp4"), new VideoOptions(30, null, true), _ => { }));
        source.Capture!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_audio_device_lister_returns_an_empty_list_when_ffmpeg_cannot_run()
    {
        var lister = new FfmpegAudioDeviceLister(new DirectShowGdigrab(() => true));
        (await lister.ListAsync(_folder.File("not-ffmpeg.exe"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_audio_device_lister_runs_ffmpeg_and_parses_its_output()
    {
        var ffmpeg = RequireFfmpeg();
        var platform = new TestSourcePlatform();
        await new FfmpegAudioDeviceLister(platform).ListAsync(ffmpeg);
        platform.ListedOutput.ShouldContain("ffmpeg version");
    }

    private static async Task<(int ExitCode, string Output)> RunFfmpegAsync(string ffmpeg, params string[] args)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stderr + await stdout);
    }

    /// <summary>The time of the last packet of a stream, in seconds, read from ffmpeg's framecrc output.</summary>
    private static async Task<double> StreamDurationAsync(string ffmpeg, string file, string stream)
    {
        var (_, output) = await RunFfmpegAsync(ffmpeg, "-v", "error", "-i", file, "-map", "0:" + stream, "-c", "copy", "-f", "framecrc", "-");
        var last = output.Split('\n').Last(l => l.Length > 0 && !l.StartsWith('#'));
        // stream_index, dts, pts, duration, size, hash — with a "#tb" header giving the time base.
        var timeBase = output.Split('\n').First(l => l.StartsWith("#tb", StringComparison.Ordinal)).Split(':')[1].Trim().Split('/');
        var fields = last.Split(',').Select(f => f.Trim()).ToArray();
        var pts = long.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture);
        var duration = long.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture);
        return (pts + duration) * double.Parse(timeBase[0], System.Globalization.CultureInfo.InvariantCulture)
               / double.Parse(timeBase[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A platform whose "screen" is ffmpeg's test pattern.</summary>
    private sealed class TestSourcePlatform : IFfmpegPlatform
    {
        public PixelRect? Area { get; private set; }
        public string ListedOutput { get; private set; } = "";
        public string SystemAudioDevice => "none";
        public string SystemAudioRequirement => "";
        public string InstallHint => "";
        public IReadOnlyList<string> ListDevicesArguments { get; } = ["-version"];

        public IReadOnlyList<string> ParseDeviceList(string output)
        {
            ListedOutput = output;
            return [];
        }

        public bool CanRecordSystemAudio(IReadOnlyCollection<string> devices) => false;

        public PipedAudio? SystemAudio { get; private set; }

        public PipedAudio? Microphone { get; private set; }

        public CaptureInputs BuildInputs(PixelRect area, VideoOptions options, PipedAudio? systemAudio, PipedAudio? microphone = null)
        {
            Area = area;
            SystemAudio = systemAudio;
            Microphone = microphone;
            return TestSources(audio: false);
        }
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public PipedAudio Input { get; } = new(@"\\.\pipe\_none", new RawAudioFormat("f32le", 48000, 2));

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSource : ISystemAudioSource
    {
        public FakeCapture? Capture { get; private set; }

        public bool IsAvailable() => true;

        public IAudioCapture Start() => Capture = new FakeCapture();
    }

    /// <summary>The ffmpeg end of a capture: a pipe that ffmpeg reads, fed with silence for a while or until the end.</summary>
    private sealed class TestPipe : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly CancellationTokenSource _stop = new();

        public TestPipe(TimeSpan? feedFor)
        {
            // Elsewhere .NET's named pipes are Unix sockets, which ffmpeg cannot open by that name.
            Assert.SkipUnless(OperatingSystem.IsWindows(), "ffmpeg reads Windows named pipes only.");
            var name = "showtime-test-" + Guid.NewGuid().ToString("N");
            Path = @"\\.\pipe\" + name;
            _server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ = Task.Run(() => ServeAsync(feedFor));
        }

        public string Path { get; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<string> Input =>
            ["-f", "f32le", "-ar", "48000", "-ac", "2", "-probesize", "32", "-analyzeduration", "0", "-i", Path];

        private async Task ServeAsync(TimeSpan? feedFor)
        {
            try
            {
                await _server.WaitForConnectionAsync(_stop.Token);
                // 20 ms of stereo float silence every 20 ms, as a capture would write it.
                var chunk = new byte[48000 / 50 * 8];
                var clock = Stopwatch.StartNew();
                while (feedFor is null || clock.Elapsed < feedFor)
                {
                    await _server.WriteAsync(chunk, _stop.Token);
                    await Task.Delay(20, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // ffmpeg closed the pipe, or the test is over.
            }
        }

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            _stop.Cancel();
            _server.Dispose();
            _stop.Dispose();
        }
    }
}
