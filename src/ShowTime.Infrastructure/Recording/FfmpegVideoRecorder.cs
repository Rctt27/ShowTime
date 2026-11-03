using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;

namespace ShowTime.Infrastructure.Recording;

/// <summary>One ffmpeg process recording to an MP4 file.</summary>
public sealed partial class FfmpegVideoRecorder : IVideoRecorder
{
    /// <summary>How long ffmpeg gets to stop on its own before the sound's pipes are closed.</summary>
    private static readonly TimeSpan StarvedPipeDelay = TimeSpan.FromSeconds(3);

    private readonly Process _process;
    private readonly IDisposable? _audio;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Action<IVideoRecorder> _onUnexpectedExit;
    private readonly TaskCompletionSource<bool> _capturing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private volatile bool _noVideo;

    private FfmpegVideoRecorder(Process process, IDisposable? audio, string outputPath, Action<IVideoRecorder> onUnexpectedExit)
    {
        _process = process;
        _audio = audio;
        OutputPath = outputPath;
        _onUnexpectedExit = onUnexpectedExit;
    }

    public string OutputPath { get; }

    public string ErrorTail => string.Join("\n", _log.ToArray().TakeLast(6));

    public Task<bool> Capturing => _capturing.Task;

    /// <param name="ffmpegPath">The ffmpeg executable.</param>
    /// <param name="inputs">The platform's screen and audio inputs.</param>
    /// <param name="outputPath">The MP4 to write.</param>
    /// <param name="onUnexpectedExit">ffmpeg stopped on its own; called on a worker thread.</param>
    /// <param name="audio">The captures feeding the inputs, freed once ffmpeg is done with them.</param>
    /// <param name="microphoneTrackPath">A WAV file to write the microphone to, alone, for the transcription.</param>
    public static FfmpegVideoRecorder Start(string ffmpegPath, CaptureInputs inputs, string outputPath, Action<IVideoRecorder> onUnexpectedExit,
        IDisposable? audio = null, string? microphoneTrackPath = null)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in RecordingCommand.Build(inputs, outputPath, microphoneTrackPath))
            start.ArgumentList.Add(arg);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var recorder = new FfmpegVideoRecorder(process, audio, outputPath, onUnexpectedExit);
        process.ErrorDataReceived += (_, e) => recorder.OnErrorLine(e.Data);
        process.Exited += (_, _) => recorder.OnExited();

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        process.BeginErrorReadLine();
        return recorder;
    }

    /// <summary>"q" on stdin is ffmpeg's clean stop: it writes the MP4 index, unlike killing the process.</summary>
    public async Task<bool> StopAsync()
    {
        _stopping = true;
        try
        {
            await _process.StandardInput.WriteAsync('q');
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already exited.
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // The sound captures are freed only once ffmpeg is done: it reads those inputs up to their last frame. But a
            // pipe that stays empty leaves ffmpeg blocked reading it, deaf to "q"; closing it ends that input.
            var exit = _process.WaitForExitAsync(timeout.Token);
            if (_audio is not null && await Task.WhenAny(exit, Task.Delay(StarvedPipeDelay, timeout.Token)) != exit)
                _audio.Dispose();
            await exit;
        }
        catch (OperationCanceledException)
        {
            Kill();
            return false;
        }
        finally
        {
            _audio?.Dispose();
        }
        // A sound input silent from the start holds every frame in the muxer: ffmpeg then writes an MP4 with exit code
        // 0 but without a single frame, and says so of the video, always the first output stream.
        return !_noVideo && _process.ExitCode == 0 && File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0;
    }

    public void Kill()
    {
        _stopping = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
        _audio?.Dispose();
    }

    public void Dispose()
    {
        _audio?.Dispose();
        _process.Dispose();
    }

    internal static bool SaysNoVideoWasWritten(string line) => NoVideoWritten().IsMatch(line);

    private void OnErrorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (SaysNoVideoWasWritten(line))
            _noVideo = true;
        if (line.Contains(RecordingCommand.CapturingLine, StringComparison.Ordinal))
            _capturing.TrySetResult(true);
        // The info level is only read for that line; the log keeps warnings and errors.
        if (InfoLevel().IsMatch(line))
            return;
        _log.Enqueue(ProblemLevel().Replace(line, ""));
        while (_log.Count > 50)
            _log.TryDequeue(out _);
    }

    private void OnExited()
    {
        _capturing.TrySetResult(false);
        if (_stopping)
            return;
        // Exited can come before the last stderr lines, which usually hold the reason; this waits for them.
        _process.WaitForExit();
        _onUnexpectedExit(this);
    }

    /// <summary>The video is the first output stream. ffmpeg 5 names it in the text, 6.1 and later in the prefix.</summary>
    [GeneratedRegex(@"Finishing stream 0:0 without any data written to it|vost#0:0[^\]]*\].*Finishing stream without any data written to it")]
    private static partial Regex NoVideoWritten();

    [GeneratedRegex(@"\[(info|verbose|debug|trace)\] ")]
    private static partial Regex InfoLevel();

    [GeneratedRegex(@"\[(warning|error|fatal|panic)\] ")]
    private static partial Regex ProblemLevel();
}
