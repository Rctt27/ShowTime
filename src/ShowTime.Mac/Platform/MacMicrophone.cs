using System.Runtime.InteropServices;
using AudioToolbox;
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac.Platform;

/// <summary>
/// The microphone, captured by AVFoundation and written to a FIFO for ffmpeg. ffmpeg's own avfoundation input keeps a
/// single 10 ms buffer of it, overwritten whenever ffmpeg reads late: the voice came out full of holes, played back
/// too fast.
/// </summary>
internal sealed class MacMicrophone(ILogger<MacMicrophone> log) : IMicrophoneSource
{
    public IAudioCapture Start(string microphone) => new MacMicrophoneCapture(microphone, log);
}

/// <summary>
/// One recording's microphone. The session starts at once, so that it is running when ffmpeg opens the FIFO (what
/// comes before is dropped by the pipe), and stops on disposal. Whatever fails in it (permission refused, microphone
/// gone), the pipe goes on with silence: the video is recorded, with the microphone muted.
/// </summary>
internal sealed class MacMicrophoneCapture : IAudioCapture
{
    /// <summary>The format asked of AVFoundation and declared to ffmpeg.</summary>
    private static readonly RawAudioFormat Format = new("f32le", 48000, 1);

    /// <summary>The pipe's clock; the samples' times are converted to it.</summary>
    private static readonly CMClock HostClock = CMClock.HostTimeClock;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _log;
    private readonly MacAudioPipe _pipe;

    /// <summary>
    /// Never disposed: AVFoundation may still call it after the stop, and a call into a disposed object would crash
    /// the app. The garbage collector frees it once the output lets it go.
    /// </summary>
    private readonly SampleOutput _output;

    private readonly Lock _gate = new();
    private readonly Task _starting;
    private AVCaptureSession? _session;
    private bool _disposed;

    public MacMicrophoneCapture(string microphone, ILogger log)
    {
        _log = log;
        _pipe = new MacAudioPipe(Path.GetTempPath(), Format, Now);
        _output = new SampleOutput(_pipe, Now, log);
        // startRunning blocks while the device opens: off the caller's thread, which counts down to the recording.
        _starting = Task.Run(() => StartSession(microphone));
    }

    public PipedAudio Input => _pipe.Input;

    private static TimeSpan Now() => TimeSpan.FromSeconds(HostClock.CurrentTime.Seconds);

    private void StartSession(string microphone)
    {
        try
        {
            if (Find(microphone) is not { } device)
            {
                Failed("no microphone", null);
                return;
            }
            var input = AVCaptureDeviceInput.FromDevice(device, out var error);
            if (input is null)
            {
                Failed("the microphone could not be opened", error?.LocalizedDescription);
                return;
            }
            var session = new AVCaptureSession();
            var output = new AVCaptureAudioDataOutput
            {
                AudioSettings = new AudioSettings
                {
                    Format = AudioFormatType.LinearPCM,
                    SampleRate = Format.SampleRate,
                    NumberChannels = Format.Channels,
                    LinearPcmBitDepth = 32,
                    LinearPcmFloat = true,
                    LinearPcmNonInterleaved = false,
                    LinearPcmBigEndian = false
                }
            };
            if (!session.CanAddInput(input) || !session.CanAddOutput(output))
            {
                Failed("the capture session refused the microphone", null);
                output.Dispose();
                input.Dispose();
                session.Dispose();
                return;
            }
            session.AddInput(input);
            session.AddOutput(output);
            output.SetSampleBufferDelegate(_output, new DispatchQueue("io.github.rctt27.showtime.microphone"));
            _output.Session = session;
            lock (_gate)
            {
                if (_disposed)
                {
                    session.Dispose();
                    return;
                }
                _session = session;
            }
            session.StartRunning();
        }
#pragma warning disable CA1031 // A throw here would leave ffmpeg waiting on an empty pipe; the microphone stays silent instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed("the capture could not be set up", ex.Message);
        }
    }

    /// <summary>The microphone ffmpeg listed under that name, or the default one when it is gone.</summary>
    private AVCaptureDevice? Find(string name)
    {
        using var discovery = AVCaptureDeviceDiscoverySession.Create(
            [AVCaptureDeviceType.Microphone, AVCaptureDeviceType.External], AVMediaTypes.Audio, AVCaptureDevicePosition.Unspecified);
        if (discovery?.Devices.FirstOrDefault(device => device.LocalizedName == name) is { } named)
            return named;
        if (_log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("The microphone {Name} was not found; the default one is recorded", name);
        return AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);
    }

    private void Failed(string what, string? error)
    {
        if (_log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("The microphone is recorded silent: {What} ({Error})", what, error);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        // A start still under way would leave the session running without an owner.
        _starting.Wait(StopTimeout);
        AVCaptureSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            session.StopRunning();
            session.Dispose();
        }
        if (_pipe.Reanchors > 0 && _log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("{Count} microphone packets had a time far from the stream's and were appended", _pipe.Reanchors);
        _pipe.Dispose();
    }

    /// <summary>Receives AVFoundation's samples, on its queue, and writes them to the pipe.</summary>
    private sealed class SampleOutput(MacAudioPipe pipe, Func<TimeSpan> clock, ILogger log) : AVCaptureAudioDataOutputSampleBufferDelegate
    {
        private bool _firstLogged;
        private bool _problemLogged;
        private CMClock? _sessionClock;

        /// <summary>The session whose clock stamps the samples.</summary>
        public AVCaptureSession? Session { get; set; }

        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            try
            {
                Write(sampleBuffer);
            }
#pragma warning disable CA1031 // A throw on AVFoundation's queue would end the app mid-recording.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Problem("a microphone packet could not be read: " + ex.Message);
            }
            finally
            {
                // Released at once, rather than by the garbage collector: AVFoundation stops sending once its few
                // buffers are all held.
                sampleBuffer.Dispose();
            }
        }

        private void Write(CMSampleBuffer sampleBuffer)
        {
            if (!sampleBuffer.IsValid || sampleBuffer.NumSamples <= 0)
                return;
            if (sampleBuffer.GetAudioFormatDescription()?.AudioStreamBasicDescription is not { } format ||
                format.BitsPerChannel != 32 || (int)format.SampleRate != Format.SampleRate || format.ChannelsPerFrame != Format.Channels)
            {
                Problem("unexpected audio format from AVFoundation");
                return;
            }
            if (sampleBuffer.GetDataBuffer() is not { } data)
                return;
            var expected = (nuint)(sampleBuffer.NumSamples * sizeof(float) * Format.Channels);
            if (data.DataLength != expected)
            {
                Problem($"a microphone packet of {data.DataLength} bytes instead of {expected}");
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
            var at = TimeSpan.FromSeconds(HostTime(sampleBuffer.PresentationTimeStamp).Seconds);
            if (!_firstLogged && log.IsEnabled(LogLevel.Information))
            {
                // Confirms by hand that the samples' times and the host clock share a base: a few tens of ms expected.
                log.LogInformation("First microphone packet: {Offset:F0} ms before the host clock", (clock() - at).TotalMilliseconds);
            }
            _firstLogged = true;
            pipe.Write(at, bytes);
        }

        /// <summary>The session stamps the samples on its synchronization clock, usually the host clock itself.</summary>
        private CMTime HostTime(CMTime time)
        {
            _sessionClock ??= Session?.SynchronizationClock;
            return _sessionClock is { } sessionClock ? CMClockOrTimebase.ConvertTime(time, sessionClock, HostClock) : time;
        }

        private void Problem(string what)
        {
            if (!_problemLogged && log.IsEnabled(LogLevel.Warning))
                log.LogWarning("The microphone is skipped: {What}", what);
            _problemLogged = true;
        }
    }
}
