using System.Runtime.InteropServices;
using CoreFoundation;
using CoreMedia;
using Foundation;
using Microsoft.Extensions.Logging;
using ScreenCaptureKit;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac.Platform;

/// <summary>
/// What the Mac plays, captured by ScreenCaptureKit and written to a FIFO for ffmpeg: no audio driver to install.
/// Replaces BlackHole, as WASAPI loopback does on Windows.
/// </summary>
internal sealed class MacSystemAudio(ILogger<MacSystemAudio> log) : ISystemAudioSource
{
    /// <summary>ScreenCaptureKit captures audio from macOS 13; ShowTime needs 14.</summary>
    public bool IsAvailable() => true;

    public IAudioCapture Start() => new MacSystemAudioCapture(log);
}

/// <summary>
/// One recording's capture: ScreenCaptureKit starts when ffmpeg opens the FIFO, and stops on disposal. Whatever fails
/// in it (permission withdrawn, a native callback throwing), the pipe goes on with silence: the video is recorded, with
/// the Mac's sound muted, rather than the app stopping mid-recording with an unplayable file.
/// </summary>
internal sealed class MacSystemAudioCapture : IAudioCapture
{
    /// <summary>The format asked of ScreenCaptureKit and declared to ffmpeg.</summary>
    private static readonly RawAudioFormat Format = new("f32le", 48000, 2);

    /// <summary>ScreenCaptureKit stamps its samples on the host clock.</summary>
    private static readonly CMClock HostClock = CMClock.HostTimeClock;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _log;
    private readonly MacAudioPipe _pipe;

    /// <summary>
    /// Never disposed: ScreenCaptureKit may still call it after a late stop, and a call into a disposed object would
    /// crash the app. The garbage collector frees it once the stream lets it go.
    /// </summary>
    private readonly SampleOutput _output;

    private readonly Lock _gate = new();
    private SCStream? _stream;

    /// <summary>Completed when StartCapture has answered: a stream still starting cannot be stopped.</summary>
    private TaskCompletionSource? _started;

    private bool _disposed;

    public MacSystemAudioCapture(ILogger log)
    {
        _log = log;
        _pipe = new MacAudioPipe(Path.GetTempPath(), Format, Now);
        _output = new SampleOutput(_pipe, Now, log);
        _pipe.Opened += StartStream;
    }

    public PipedAudio Input => _pipe.Input;

    private static TimeSpan Now() => TimeSpan.FromSeconds(HostClock.CurrentTime.Seconds);

    /// <summary>On the pipe's thread, once ffmpeg has opened it.</summary>
    private void StartStream() =>
        SCShareableContent.GetShareableContent(true, true, (content, error) =>
        {
            try
            {
                Start(content, error);
            }
#pragma warning disable CA1031 // A throw on ScreenCaptureKit's thread would end the app; the sound stays silent instead.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Failed("the capture could not be set up: " + ex.Message, null);
            }
        });

    private void Start(SCShareableContent? content, NSError? error)
    {
        if (content?.Displays.FirstOrDefault() is not { } display)
        {
            Failed("no display to capture the sound of", error);
            return;
        }
        using var filter = new SCContentFilter(display, [], SCContentFilterOption.Exclude);
        using var configuration = new SCStreamConfiguration
        {
            CapturesAudio = true,
            SampleRate = Format.SampleRate,
            ChannelCount = Format.Channels,
            ExcludesCurrentProcessAudio = true,
            // The picture is not used: the smallest, once a second.
            Width = 2,
            Height = 2,
            MinimumFrameInterval = new CMTime(1, 1),
            ShowsCursor = false
        };
        lock (_gate)
        {
            if (_disposed)
                return;
            var stream = new SCStream(filter, configuration, null);
            var queue = new DispatchQueue("io.github.rctt27.showtime.sound");
            // A screen output too, which drops the frames: without one, ScreenCaptureKit logs each frame it drops.
            if (!stream.AddStreamOutput(_output, SCStreamOutputType.Audio, queue, out var outputError) ||
                !stream.AddStreamOutput(_output, SCStreamOutputType.Screen, queue, out outputError))
            {
                Failed("the stream refused its outputs", outputError);
                stream.Dispose();
                return;
            }
            _stream = stream;
            var started = _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stream.StartCapture(startError =>
            {
                if (startError is not null)
                    Failed("the capture did not start", startError);
                started.TrySetResult();
            });
        }
    }

    private void Failed(string what, NSError? error)
    {
        if (_log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("The Mac's sound is recorded silent: {What} ({Error})", what, error?.LocalizedDescription);
    }

    public void Dispose()
    {
        SCStream? stream;
        Task? starting;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            stream = _stream;
            starting = _started?.Task;
            _stream = null;
        }
        if (stream is not null)
        {
            // A start still under way would ignore the stop and leave the stream running without an owner.
            starting?.Wait(StopTimeout);
            using var stopped = new ManualResetEventSlim();
            stream.StopCapture(_ => stopped.Set());
            stopped.Wait(StopTimeout);
            // Even if the stop is late, nothing more reaches this capture.
            stream.RemoveStreamOutput(_output, SCStreamOutputType.Audio, out _);
            stream.RemoveStreamOutput(_output, SCStreamOutputType.Screen, out _);
            stream.Dispose();
        }
        if (_pipe.Reanchors > 0 && _log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("{Count} sound packets had a time far from the stream's and were appended", _pipe.Reanchors);
        _pipe.Dispose();
    }

    /// <summary>Receives ScreenCaptureKit's samples, on its queue, and writes the audio ones to the pipe.</summary>
    private sealed class SampleOutput(MacAudioPipe pipe, Func<TimeSpan> clock, ILogger log) : NSObject, ISCStreamOutput
    {
        private bool _firstLogged;
        private bool _problemLogged;

        [Export("stream:didOutputSampleBuffer:ofType:")]
        public void DidOutputSampleBuffer(SCStream stream, CMSampleBuffer sampleBuffer, SCStreamOutputType type)
        {
            try
            {
                if (type == SCStreamOutputType.Audio)
                    Write(sampleBuffer);
            }
#pragma warning disable CA1031 // A throw on ScreenCaptureKit's queue would end the app mid-recording.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Problem("a sound packet could not be read: " + ex.Message);
            }
        }

        private void Write(CMSampleBuffer sampleBuffer)
        {
            if (!sampleBuffer.IsValid || sampleBuffer.NumSamples <= 0)
                return;
            if (sampleBuffer.GetAudioFormatDescription()?.AudioStreamBasicDescription is not { } format ||
                format.BitsPerChannel != 32 || (int)format.SampleRate != Format.SampleRate || format.ChannelsPerFrame != Format.Channels)
            {
                Problem("unexpected audio format from ScreenCaptureKit");
                return;
            }
            if (sampleBuffer.GetDataBuffer() is not { } data)
                return;
            // One 32-bit sample per channel and frame: anything else (padding between the channels) would garble them.
            var expected = (nuint)(sampleBuffer.NumSamples * sizeof(float) * format.ChannelsPerFrame);
            if (data.DataLength != expected)
            {
                Problem($"a sound packet of {data.DataLength} bytes instead of {expected}");
                return;
            }

            var bytes = new byte[(int)data.DataLength];
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                if (data.CopyDataBytes(0, data.DataLength, pinned.AddrOfPinnedObject()) != CMBlockBufferError.None)
                    return;
            }
            finally
            {
                pinned.Free();
            }
            // Non-interleaved: one channel after the other in the block buffer.
            var planar = (format.FormatFlags & AudioToolbox.AudioFormatFlags.IsNonInterleaved) != 0;
            var pcm = planar ? PlanarAudio.Interleave(Split(bytes, format.ChannelsPerFrame)) : bytes;
            var at = TimeSpan.FromSeconds(sampleBuffer.PresentationTimeStamp.Seconds);
            if (!_firstLogged && log.IsEnabled(LogLevel.Information))
            {
                // Confirms by hand that the samples' times and the host clock share a base: a few tens of ms expected.
                log.LogInformation("First Mac sound packet: {Offset:F0} ms before the host clock", (clock() - at).TotalMilliseconds);
            }
            _firstLogged = true;
            pipe.Write(at, pcm);
        }

        private void Problem(string what)
        {
            if (!_problemLogged && log.IsEnabled(LogLevel.Warning))
                log.LogWarning("The Mac's sound is skipped: {What}", what);
            _problemLogged = true;
        }

        private static byte[][] Split(byte[] bytes, int channels)
        {
            var size = bytes.Length / channels;
            return [.. Enumerable.Range(0, channels).Select(channel => bytes.AsSpan(channel * size, size).ToArray())];
        }
    }
}
