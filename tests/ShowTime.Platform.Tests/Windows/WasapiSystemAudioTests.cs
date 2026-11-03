using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using ShowTime.Core.Ports;
using ShowTime.Platform.Windows;

namespace ShowTime.Platform.Tests.Windows;

/// <summary>WASAPI loopback on this computer's default output; skipped without one (CI runners have none).</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiSystemAudioTests
{
    private readonly WasapiSystemAudio _source = new();

    public WasapiSystemAudioTests() =>
        Assert.SkipUnless(OperatingSystem.IsWindows() && _source.IsAvailable(), "Needs Windows and an audio output.");

    private static NamedPipeClientStream Connect(IAudioCapture capture) =>
        new(".", capture.Input.PipePath[@"\\.\pipe\".Length..], PipeDirection.In);

    [Fact]
    public async Task Capture_delivers_audio_at_its_declared_rate()
    {
        using var capture = _source.Start();
        using var ffmpeg = Connect(capture);
        await ffmpeg.ConnectAsync(2000, TestContext.Current.CancellationToken);

        // Measured after half a second, once the device delivers at its pace: its first packet takes a while.
        var buffer = new byte[65536];
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(0.5))
            _ = await ffmpeg.ReadAsync(buffer, TestContext.Current.CancellationToken);
        long total = 0;
        clock.Restart();
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
            total += await ffmpeg.ReadAsync(buffer, TestContext.Current.CancellationToken);

        var format = capture.Input.Format;
        var expected = 2.0 * format.SampleRate * format.BlockAlign;
        ((double)total).ShouldBeInRange(expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Dispose_without_ffmpeg_returns_promptly()
    {
        var capture = _source.Start();
        var clock = Stopwatch.StartNew();
        capture.Dispose();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Disconnect_does_not_throw()
    {
        var capture = _source.Start();
        var ffmpeg = Connect(capture);
        await ffmpeg.ConnectAsync(2000, TestContext.Current.CancellationToken);
        _ = await ffmpeg.ReadAsync(new byte[4096], TestContext.Current.CancellationToken);
        await ffmpeg.DisposeAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Should.NotThrow(capture.Dispose);
    }

    [Fact]
    public async Task Started_from_the_UI_thread_its_events_do_not_wait_for_that_thread()
    {
        // NAudio raises RecordingStopped through the SynchronizationContext it was created on: on the WPF thread, the
        // silence after a lost output would only start once that thread is free.
        var context = new CountingContext();
        IAudioCapture? capture = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            capture = _source.Start();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        using (var ffmpeg = Connect(capture!))
        {
            await ffmpeg.ConnectAsync(2000, TestContext.Current.CancellationToken);
            (await ffmpeg.ReadAsync(new byte[4096], TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);
        }
        capture!.Dispose();
        context.Posts.ShouldBe(0);
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => _posts;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
    }
}
