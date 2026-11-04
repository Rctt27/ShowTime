using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Transcription;

public enum TranscriptionState
{
    Waiting,
    Running,
    Done,
    Failed,
    Canceled
}

/// <summary>The transcription of one video's microphone track, which the video's player shows as it goes.</summary>
public sealed class TranscriptionJob
{
    internal TranscriptionJob(string videoPath, string audioPath)
    {
        VideoPath = videoPath;
        AudioPath = audioPath;
    }

    public string VideoPath { get; }

    public string AudioPath { get; }

    public TranscriptionState State { get; private set; }

    /// <summary>From 0 to 1, while running.</summary>
    public double Progress { get; private set; }

    /// <summary>Why it failed, in French for the user.</summary>
    public string? Error { get; private set; }

    /// <summary>Once done: the cues written to the .srt, none when nothing was said.</summary>
    public IReadOnlyList<SubtitleCue> Cues { get; private set; } = [];

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? Changed;

    internal bool CancelRequested { get; set; }

    internal void Update(TranscriptionState state, double progress = 0, string? error = null, IReadOnlyList<SubtitleCue>? cues = null)
    {
        State = state;
        Progress = progress;
        Error = error;
        Cues = cues ?? [];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Transcribes the microphone tracks of the recorded videos, one at a time, and writes each one's subtitles next to it.
/// Whisper takes the processor: nothing is transcribed while a video is being recorded, and a transcription under way
/// when a recording starts begins again afterwards. Every member is called on the UI thread. Each step of a transcription
/// is logged, so that the log tells what ShowTime was doing when something went wrong.
/// </summary>
public sealed class Transcriptions : IDisposable
{
    private readonly ITranscriber _transcriber;
    private readonly IAudioExtractor _extractor;
    private readonly MicrophoneTracks _tracks;
    private readonly TranscriptionModelLocator _models;
    private readonly AppSettings _settings;
    private readonly SubtitleFile _subtitles;
    private readonly IFileSystem _fileSystem;
    private readonly IRecordingStatus _recording;
    private readonly IUiThread _uiThread;
    private readonly ILogger<Transcriptions> _log;
    private readonly List<TranscriptionJob> _waiting = [];
    private TranscriptionJob? _running;
    private CancellationTokenSource? _cancel;
    private bool _disposed;

    public Transcriptions(
        ITranscriber transcriber, IAudioExtractor extractor, MicrophoneTracks tracks, TranscriptionModelLocator models,
        AppSettings settings, SubtitleFile subtitles, IFileSystem fileSystem, IRecordingStatus recording, IUiThread uiThread,
        ILogger<Transcriptions> log)
    {
        _transcriber = transcriber;
        _extractor = extractor;
        _tracks = tracks;
        _models = models;
        _settings = settings;
        _subtitles = subtitles;
        _fileSystem = fileSystem;
        _recording = recording;
        _uiThread = uiThread;
        _log = log;
        recording.StateChanged += OnRecordingChanged;
    }

    /// <summary>Whether a model is there to transcribe with.</summary>
    public bool CanTranscribe => _models.Find(_settings.TranscriptionModelPath) is not null;

    /// <summary>Transcribes the video's microphone track, then deletes it.</summary>
    /// <param name="audioPath">
    /// The track recorded with the video; null, or a track that is gone, to extract the video's own sound first.
    /// </param>
    public TranscriptionJob Enqueue(string videoPath, string? audioPath = null)
    {
        var job = new TranscriptionJob(videoPath, audioPath ?? _tracks.NewPath());
        _waiting.Add(job);
        StartNext();
        return job;
    }

    /// <summary>The video is gone: its transcription is abandoned and its track deleted.</summary>
    public void Cancel(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job == _running)
        {
            job.CancelRequested = true;
            _cancel?.Cancel();
        }
        else if (job.State is TranscriptionState.Waiting or TranscriptionState.Failed)
        {
            _waiting.Remove(job);
            TryDelete(job.AudioPath);
            job.Update(TranscriptionState.Canceled);
            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("Transcription of {Video} canceled", Name(job));
        }
    }

    public void Retry(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State != TranscriptionState.Failed)
            return;
        _waiting.Add(job);
        job.Update(TranscriptionState.Waiting);
        StartNext();
    }

    /// <summary>A track the player kept for its button, never transcribed: deleted with the player.</summary>
    public void DiscardTrack(string audioPath) => TryDelete(audioPath);

    public void Dispose()
    {
        _disposed = true;
        _recording.StateChanged -= OnRecordingChanged;
        _cancel?.Cancel();
    }

    private void OnRecordingChanged(object? sender, EventArgs e)
    {
        if (!_recording.IsRecording)
            StartNext();
        else if (_running is not null)
            _cancel?.Cancel();
    }

    private void StartNext()
    {
        if (_disposed || _running is not null || _recording.IsRecording || _waiting.Count == 0)
            return;
        var job = _waiting[0];
        _waiting.RemoveAt(0);
        _ = RunAsync(job);
    }

    private async Task RunAsync(TranscriptionJob job)
    {
        if (_models.Find(_settings.TranscriptionModelPath) is not { } model)
        {
            job.Update(TranscriptionState.Failed, error:
                "Le modèle de transcription est introuvable : réinstallez ShowTime avec son dossier « models », " +
                "ou indiquez un modèle dans les réglages.");
            if (_log.IsEnabled(LogLevel.Warning))
                _log.LogWarning("Transcription of {Video} failed: no Whisper model", Name(job));
            StartNext();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _running = job;
        _cancel = cancel;
        job.Update(TranscriptionState.Running);
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Transcription of {Video} started", Name(job));

        TranscriptionState outcome;
        string? error = null;
        Exception? failure = null;
        IReadOnlyList<SubtitleCue>? cues = null;
        try
        {
            if (!_fileSystem.File.Exists(job.AudioPath))
                await _extractor.ExtractAsync(job.VideoPath, job.AudioPath, cancel.Token);
            var words = await _transcriber.TranscribeAsync(job.AudioPath, model, _settings.TranscriptionLanguage,
                progress => _uiThread.Post(() =>
                {
                    if (job == _running)
                        job.Update(TranscriptionState.Running, progress);
                }),
                cancel.Token);
            cues = new SubtitleDocument(SubtitleSegmenter.Segment(words, SubtitleProfile.Tutorial)).Cues;
            // Nothing said: no file, which the player shows as a video without subtitles yet.
            if (cues.Count > 0)
                _subtitles.Save(job.VideoPath, cues);
            TryDelete(job.AudioPath);
            outcome = TranscriptionState.Done;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (job.CancelRequested)
            {
                TryDelete(job.AudioPath);
                outcome = TranscriptionState.Canceled;
            }
            else
            {
                // Interrupted by a recording: first in line once it is over.
                _waiting.Insert(0, job);
                outcome = TranscriptionState.Waiting;
            }
        }
#pragma warning disable CA1031 // Shown in the player, which offers to try again; the track is kept for that.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = TranscriptionState.Failed;
            error = "La transcription a échoué : " + ex.Message;
            failure = ex;
        }
        LogOutcome(job, outcome, cues?.Count ?? 0, failure);

        _running = null;
        _cancel = null;
        job.CancelRequested = false;
        job.Update(outcome, error: error, cues: cues);
        StartNext();
    }

    private void LogOutcome(TranscriptionJob job, TranscriptionState outcome, int cues, Exception? failure)
    {
        if (outcome == TranscriptionState.Failed)
        {
            if (_log.IsEnabled(LogLevel.Warning))
                _log.LogWarning(failure, "Transcription of {Video} failed", Name(job));
            return;
        }
        if (!_log.IsEnabled(LogLevel.Information))
            return;
        switch (outcome)
        {
            case TranscriptionState.Done:
                _log.LogInformation("Transcription of {Video} done: {Count} subtitles", Name(job), cues);
                break;
            case TranscriptionState.Canceled:
                _log.LogInformation("Transcription of {Video} canceled", Name(job));
                break;
            default:
                _log.LogInformation("Transcription of {Video} interrupted by a recording; it starts again afterwards", Name(job));
                break;
        }
    }

    /// <summary>The video's file name: enough to follow it in the log, without the user's folders.</summary>
    private static string Name(TranscriptionJob job) => Path.GetFileName(job.VideoPath);

    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A track left behind is deleted at the next start.
        }
    }
}
