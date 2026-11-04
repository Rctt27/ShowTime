using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Core.Tests.Transcription;

public sealed class TranscriptionsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ShowTimeQueue");
    private static readonly string Models = Path.Combine(Root, "models");
    private static readonly string Model = Path.Combine(Models, "ggml-small-q5_1.bin");
    private static readonly string Work = Path.Combine(Root, "work");

    private readonly MockFileSystem _fs = new();
    private readonly AppSettings _settings = new(SettingsDefaults.ForWindows(Root));
    private readonly FakeTranscriber _transcriber = new();
    private readonly FakeRecordingStatus _recording = new();
    private readonly ListLogger<Transcriptions> _log = new();
    private readonly FakeAudioExtractor _extractor;
    private readonly Transcriptions _transcriptions;

    public TranscriptionsTests()
    {
        _fs.AddFile(Model, new MockFileData(""));
        _extractor = new FakeAudioExtractor(_fs);
        var models = new TranscriptionModelLocator(_fs, [Models]);
        var paths = Substitute.For<IAppPaths>();
        paths.WorkFolder.Returns(Work);
        _transcriptions = new Transcriptions(_transcriber, _extractor, new MicrophoneTracks(_settings, models, paths, _fs), models,
            _settings, new SubtitleFile(_fs), _fs, _recording, new ImmediateUiThread(), _log);
    }

    private static SubtitleCue Cue(double start, double end, string text) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);
    private static TranscribedWord Word(double start, double end, string text) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    /// <summary>A recorded video and the microphone track waiting for its transcription.</summary>
    private (string Video, string Audio) Recorded(string name)
    {
        var video = Path.Combine(Root, name + ".mp4");
        var audio = Path.Combine(Root, "work", name + ".wav");
        _fs.AddFile(video, new MockFileData(""));
        _fs.AddFile(audio, new MockFileData(""));
        return (video, audio);
    }

    [Fact]
    public void A_transcription_writes_the_subtitles_next_to_the_video_and_deletes_the_track()
    {
        var (video, audio) = Recorded("a");
        _settings.TranscriptionLanguage = "auto";

        var job = _transcriptions.Enqueue(video, audio);

        job.State.ShouldBe(TranscriptionState.Running);
        _transcriber.Last.AudioPath.ShouldBe(audio);
        _transcriber.Last.ModelPath.ShouldBe(Model);
        _transcriber.Last.Language.ShouldBe("auto");

        _transcriber.Last.Complete(Word(0, 1, " Bonjour."), Word(2, 3, " [Musique]"));

        job.State.ShouldBe(TranscriptionState.Done);
        // Cut by the tutorial profile: the cue lingers half a second after the word.
        job.Cues.ShouldBe([Cue(0, 1.5, "Bonjour.")]);
        new SubtitleFile(_fs).Load(video)!.Cues.ShouldBe(job.Cues);
        _fs.File.Exists(audio).ShouldBeFalse();
    }

    [Fact]
    public void A_video_without_a_microphone_track_has_its_sound_extracted_first()
    {
        // Recorded before the track was kept, or with its track gone: its sound is in the MP4.
        var video = Path.Combine(Root, "old.mp4");
        _fs.AddFile(video, new MockFileData(""));

        var job = _transcriptions.Enqueue(video);

        var (extractedFrom, extractedTo) = _extractor.Calls.ShouldHaveSingleItem();
        extractedFrom.ShouldBe(video);
        Path.GetDirectoryName(extractedTo).ShouldBe(Work);
        _transcriber.Last.AudioPath.ShouldBe(extractedTo);

        _transcriber.Last.Complete(Word(0, 1, "Bonjour."));

        job.State.ShouldBe(TranscriptionState.Done);
        _fs.File.Exists(extractedTo).ShouldBeFalse();
    }

    [Fact]
    public void A_recorded_track_is_used_as_it_is()
    {
        var (video, audio) = Recorded("a");

        _transcriptions.Enqueue(video, audio);

        _extractor.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_sound_that_cannot_be_extracted_fails_with_its_reason()
    {
        var video = Path.Combine(Root, "silent.mp4");
        _fs.AddFile(video, new MockFileData(""));
        _extractor.Failure = new InvalidOperationException("La vidéo n'a pas de son à transcrire.");

        var job = _transcriptions.Enqueue(video);

        job.State.ShouldBe(TranscriptionState.Failed);
        job.Error.ShouldBe("La transcription a échoué : La vidéo n'a pas de son à transcrire.");
        _transcriber.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Whether_it_can_transcribe_depends_on_the_model()
    {
        _transcriptions.CanTranscribe.ShouldBeTrue();
        _fs.File.Delete(Model);
        _transcriptions.CanTranscribe.ShouldBeFalse();
    }

    [Fact]
    public void A_track_never_transcribed_is_deleted_when_discarded()
    {
        var (_, audio) = Recorded("a");

        _transcriptions.DiscardTrack(audio);

        _fs.File.Exists(audio).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_said_writes_no_subtitle_file()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);

        _transcriber.Last.Complete();

        job.State.ShouldBe(TranscriptionState.Done);
        job.Cues.ShouldBeEmpty();
        _fs.File.Exists(SubtitleFile.PathFor(video)).ShouldBeFalse();
    }

    [Fact]
    public void Progress_is_reported_on_the_job()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);
        var changes = 0;
        job.Changed += (_, _) => changes++;

        _transcriber.Last.OnProgress(0.42);

        job.Progress.ShouldBe(0.42);
        changes.ShouldBe(1);
    }

    [Fact]
    public void Transcriptions_run_one_at_a_time_in_order()
    {
        var a = Recorded("a");
        var b = Recorded("b");
        var first = _transcriptions.Enqueue(a.Video, a.Audio);
        var second = _transcriptions.Enqueue(b.Video, b.Audio);

        second.State.ShouldBe(TranscriptionState.Waiting);
        _transcriber.Calls.Count.ShouldBe(1);

        _transcriber.Last.Complete();

        first.State.ShouldBe(TranscriptionState.Done);
        second.State.ShouldBe(TranscriptionState.Running);
        _transcriber.Last.AudioPath.ShouldBe(b.Audio);
    }

    [Fact]
    public void Nothing_is_transcribed_while_a_video_is_being_recorded()
    {
        _recording.Set(recording: true);
        var (video, audio) = Recorded("a");

        var job = _transcriptions.Enqueue(video, audio);

        job.State.ShouldBe(TranscriptionState.Waiting);
        _transcriber.Calls.ShouldBeEmpty();

        _recording.Set(recording: false);

        job.State.ShouldBe(TranscriptionState.Running);
    }

    [Fact]
    public void A_recording_that_starts_interrupts_the_transcription_which_starts_again_afterwards()
    {
        // Whisper takes every core: the recording would drop frames.
        var a = Recorded("a");
        var b = Recorded("b");
        var first = _transcriptions.Enqueue(a.Video, a.Audio);
        _transcriptions.Enqueue(b.Video, b.Audio);

        _recording.Set(recording: true);

        _transcriber.Calls[0].Token.IsCancellationRequested.ShouldBeTrue();
        first.State.ShouldBe(TranscriptionState.Waiting);
        _fs.File.Exists(a.Audio).ShouldBeTrue();

        _recording.Set(recording: false);

        first.State.ShouldBe(TranscriptionState.Running);
        _transcriber.Last.AudioPath.ShouldBe(a.Audio);
    }

    [Fact]
    public void Cancelling_the_running_transcription_deletes_its_track_and_starts_the_next()
    {
        var a = Recorded("a");
        var b = Recorded("b");
        var first = _transcriptions.Enqueue(a.Video, a.Audio);
        var second = _transcriptions.Enqueue(b.Video, b.Audio);

        _transcriptions.Cancel(first);

        first.State.ShouldBe(TranscriptionState.Canceled);
        _fs.File.Exists(a.Audio).ShouldBeFalse();
        second.State.ShouldBe(TranscriptionState.Running);
    }

    [Fact]
    public void Cancelling_a_waiting_transcription_removes_it_from_the_transcriptions()
    {
        var a = Recorded("a");
        var b = Recorded("b");
        _transcriptions.Enqueue(a.Video, a.Audio);
        var second = _transcriptions.Enqueue(b.Video, b.Audio);

        _transcriptions.Cancel(second);
        _transcriber.Last.Complete();

        second.State.ShouldBe(TranscriptionState.Canceled);
        _fs.File.Exists(b.Audio).ShouldBeFalse();
        _transcriber.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public void A_failure_keeps_the_track_so_that_it_can_be_tried_again()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);

        _transcriber.Last.Result.SetException(new InvalidOperationException("mémoire insuffisante"));

        job.State.ShouldBe(TranscriptionState.Failed);
        job.Error.ShouldBe("La transcription a échoué : mémoire insuffisante");
        _fs.File.Exists(audio).ShouldBeTrue();

        _transcriptions.Retry(job);

        job.State.ShouldBe(TranscriptionState.Running);
        _transcriber.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public void Without_a_model_the_transcription_fails_with_a_hint()
    {
        _fs.File.Delete(Model);
        var (video, audio) = Recorded("a");

        var job = _transcriptions.Enqueue(video, audio);

        job.State.ShouldBe(TranscriptionState.Failed);
        job.Error!.ShouldStartWith("Le modèle de transcription est introuvable");
        _transcriber.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Cancelling_a_failed_transcription_deletes_its_track()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);
        _transcriber.Last.Result.SetException(new InvalidOperationException("x"));

        _transcriptions.Cancel(job);

        job.State.ShouldBe(TranscriptionState.Canceled);
        _fs.File.Exists(audio).ShouldBeFalse();
    }

    [Fact]
    public void Retry_and_cancel_of_a_finished_transcription_do_nothing()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);
        _transcriber.Last.Complete(Word(0, 1, "Un"));

        _transcriptions.Retry(job);
        _transcriptions.Cancel(job);

        job.State.ShouldBe(TranscriptionState.Done);
        _transcriber.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public void Dispose_stops_the_running_transcription()
    {
        var (video, audio) = Recorded("a");
        _transcriptions.Enqueue(video, audio);

        _transcriptions.Dispose();

        _transcriber.Last.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void A_transcription_logs_its_start_and_its_end()
    {
        var (video, audio) = Recorded("a");

        _transcriptions.Enqueue(video, audio);
        _transcriber.Last.Complete(Word(0, 1, " Bonjour."));

        _log.Entries.Count.ShouldBe(2);
        _log.Entries.ShouldAllBe(e => e.Level == LogLevel.Information && e.Message.Contains("a.mp4"));
        _log.Entries[0].Message.ShouldContain("started");
        _log.Entries[1].Message.ShouldContain("done");
    }

    [Fact]
    public void Progress_is_not_logged()
    {
        var (video, audio) = Recorded("a");
        _transcriptions.Enqueue(video, audio);

        _transcriber.Last.OnProgress(0.1);
        _transcriber.Last.OnProgress(0.2);
        _transcriber.Last.OnProgress(0.3);

        _log.Entries.Count.ShouldBe(1);
        _log.Entries[0].Message.ShouldContain("started");
    }

    [Fact]
    public void A_cancelled_transcription_is_logged()
    {
        var (video, audio) = Recorded("a");
        var job = _transcriptions.Enqueue(video, audio);

        _transcriptions.Cancel(job);

        _log.Entries[^1].Level.ShouldBe(LogLevel.Information);
        _log.Entries[^1].Message.ShouldContain("canceled");
    }

    [Fact]
    public void A_transcription_interrupted_by_a_recording_is_logged()
    {
        var (video, audio) = Recorded("a");
        _transcriptions.Enqueue(video, audio);

        _recording.Set(recording: true);

        _log.Entries[^1].Message.ShouldContain("interrupted");
    }

    [Fact]
    public void A_failed_transcription_is_logged_as_a_warning_with_its_exception()
    {
        var (video, audio) = Recorded("a");
        _transcriptions.Enqueue(video, audio);
        var failure = new InvalidOperationException("boom");

        _transcriber.Last.Result.SetException(failure);

        _log.Entries[^1].Level.ShouldBe(LogLevel.Warning);
        _log.Entries[^1].Message.ShouldContain("a.mp4");
        _log.Entries[^1].Exception.ShouldBeSameAs(failure);
    }
}
