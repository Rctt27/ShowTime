using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;

namespace ShowTime.Platform.Windows;

/// <summary>
/// What the computer plays, captured by WASAPI loopback on the default output and written raw to a named pipe for
/// ffmpeg: no audio driver to install.
/// </summary>
public sealed class WasapiSystemAudio : ISystemAudioSource
{
    public bool IsAvailable()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException)
        {
            return false;
        }
    }

    public IAudioCapture Start()
    {
        try
        {
            // Built on a pool thread: NAudio raises its events through the SynchronizationContext it is created on,
            // and on the UI thread the silence after a lost output would wait for that thread.
            return Task.Run(() => new WasapiSystemAudioCapture()).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Le son de l'ordinateur n'a pas pu être capté : " + ex.Message, ex);
        }
    }
}

internal sealed class WasapiSystemAudioCapture : IAudioCapture
{
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly WasapiOut? _silence;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _gate = new();
    private Timer? _padding;
    private bool _disposed;

    public WasapiSystemAudioCapture()
    {
        try
        {
            using (var devices = new MMDeviceEnumerator())
                _device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _capture = new WasapiLoopbackCapture(_device);
            var wave = _capture.WaveFormat;
            var isFloat = wave.Encoding == WaveFormatEncoding.IeeeFloat ||
                          (wave is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            var format = RawAudioFormat.From(isFloat, wave.BitsPerSample, wave.SampleRate, wave.Channels);
            _silence = OpenSilence(_device, wave);

            var name = $"showtime-audio-{Environment.ProcessId}-{Guid.NewGuid():N}";
            _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Input = new PipedAudio(@"\\.\pipe\" + name, format);
        }
        catch
        {
            _silence?.Dispose();
            _capture?.Dispose();
            _device?.Dispose();
            _cancel.Dispose();
            throw;
        }

        _capture.DataAvailable += (_, e) =>
        {
            // NAudio reuses its buffer.
            if (e.BytesRecorded > 0)
                _chunks.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                StartPadding();
        };
        _ = Task.Run(PumpAsync);
    }

    public PipedAudio Input { get; }

    /// <summary>
    /// Loopback delivers nothing while nothing plays, and ffmpeg would then squeeze the gaps out of the track: playing
    /// silence on the same output keeps packets coming, paced by the audio clock. Without it (an application holds the
    /// output in exclusive mode) the recording goes on.
    /// </summary>
    private static WasapiOut? OpenSilence(MMDevice device, WaveFormat format)
    {
        var silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        try
        {
            silence.Init(new SilenceProvider(format));
            return silence;
        }
        catch (COMException)
        {
            silence.Dispose();
            return null;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);
            StartCapture();
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(_cancel.Token).ConfigureAwait(false))
                await _pipe.WriteAsync(chunk, _cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // ffmpeg closed the pipe (stop, crash, kill), or this capture was disposed.
        }
    }

    private void StartCapture()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                _silence?.Play();
                _capture.StartRecording();
            }
#pragma warning disable CA1031 // NAudio maps HRESULTs to many exception types; whichever, ffmpeg must get a stream.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Output gone or busy: the video is still recorded, with silence.
                StartPadding();
            }
        }
    }

    /// <summary>
    /// The output is lost (headset unplugged…): silence until the end keeps the track as long as the video, and keeps
    /// amix from raising the microphone once this input ends.
    /// </summary>
    private void StartPadding()
    {
        lock (_gate)
        {
            if (_disposed || _padding is not null)
                return;
            var clock = Stopwatch.StartNew();
            long written = 0;
            var counter = new Lock();
            _padding = new Timer(_ =>
            {
                lock (counter)
                {
                    var frames = SilencePadding.FramesDue(clock.Elapsed, written, Input.Format.SampleRate);
                    if (frames == 0)
                        return;
                    written += frames;
                    _chunks.Writer.TryWrite(new byte[frames * Input.Format.BlockAlign]);
                }
            }, null, 0, 20);
        }
    }

    /// <summary>Waits for NAudio's capture and playback threads to end: up to a few hundred milliseconds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _padding?.Dispose();
        }
        _cancel.Cancel();
        _chunks.Writer.TryComplete();
        _capture.Dispose();
        _silence?.Dispose();
        _pipe.Dispose();
        _device.Dispose();
        _cancel.Dispose();
    }
}
