using System.IO.Abstractions;
using ShowTime.Core.Recording;
using ShowTime.Infrastructure.Platform;
using ShowTime.Infrastructure.Recording;
using ShowTime.Infrastructure.Transcription;

namespace ShowTime.Infrastructure.IntegrationTests.Transcription;

/// <summary>
/// Transcribes a short French sentence with a real Whisper model: the one in SHOWTIME_WHISPER_MODEL (CI downloads the
/// smallest), otherwise the one ShowTime would use. Skipped without a model.
/// </summary>
public sealed class WhisperTranscriberTests
{
    private static readonly string Speech = Path.Combine(AppContext.BaseDirectory, "fixtures", "speech", "bonjour-fr.wav");

    private static string RequireModel()
    {
        var model = Environment.GetEnvironmentVariable("SHOWTIME_WHISPER_MODEL") is { Length: > 0 } configured
            ? configured
            : AppPaths.ForCurrentPlatform().CreateModelLocator(new FileSystem()).Find("");
        Assert.SkipWhen(model is null || !File.Exists(model), "No Whisper model on this computer.");
        return model!;
    }

    [Fact]
    public async Task A_french_sentence_is_transcribed_word_by_word_with_its_times()
    {
        var model = RequireModel();
        var progress = new List<double>();

        var words = await new WhisperTranscriber().TranscribeAsync(Speech, model, "fr", p =>
        {
            lock (progress)
                progress.Add(p);
        }, TestContext.Current.CancellationToken);

        // "Bonjour, ceci est un essai de transcription.": one segment per word, in order.
        words.Count.ShouldBeGreaterThanOrEqualTo(6);
        words.ShouldAllBe(w => !w.Text.Trim().Contains(' '));
        words[0].Text.ShouldContain("onjour", Case.Insensitive);
        words[^1].Text.ShouldContain("transcription", Case.Insensitive);
        words.Zip(words.Skip(1)).ShouldAllBe(pair => pair.First.Start <= pair.Second.Start);
        words[0].Start.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        words[^1].End.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(4));
        lock (progress)
            progress.ShouldAllBe(p => p >= 0 && p <= 1);
    }

    [Fact]
    public async Task The_microphone_track_of_a_recording_is_transcribed()
    {
        // The whole way: ffmpeg records the sentence as the microphone, beside the video, and Whisper reads the track.
        var model = RequireModel();
        var ffmpeg = AppPaths.CreateFfmpegLocator(new FileSystem()).Find("");
        Assert.SkipWhen(ffmpeg is null, "ffmpeg is not installed.");
        using var folder = new TempFolder();
        var track = folder.File("mic.wav");
        var inputs = new CaptureInputs([["-re", "-i", Speech]], ["-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30"]);

        using (var recorder = FfmpegVideoRecorder.Start(ffmpeg!, inputs, folder.File("video.mp4"), _ => { }, microphoneTrackPath: track))
        {
            (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue();
            await Task.Delay(4500, TestContext.Current.CancellationToken);
            (await recorder.StopAsync()).ShouldBeTrue();
        }
        var words = await new WhisperTranscriber().TranscribeAsync(track, model, "fr", _ => { }, TestContext.Current.CancellationToken);

        string.Join(" ", words.Select(w => w.Text)).ShouldContain("bonjour", Case.Insensitive);
    }

    [Fact]
    public async Task A_cancelled_transcription_stops()
    {
        var model = RequireModel();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            new WhisperTranscriber().TranscribeAsync(Speech, model, "fr", _ => { }, cancel.Token));
    }

    [Fact]
    public async Task A_transcription_cancelled_while_whisper_runs_ends_cancelled_and_the_next_one_works()
    {
        // A video deleted while its microphone is transcribed. whisper.cpp reports 0 % at the start of its first
        // window: the cancellation lands while it runs, and its model must not be freed under it.
        var model = RequireModel();
        using var cancel = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(
            new WhisperTranscriber().TranscribeAsync(Speech, model, "fr", progress => _ = cancel.CancelAsync(), cancel.Token));

        var words = await new WhisperTranscriber().TranscribeAsync(Speech, model, "fr", _ => { }, TestContext.Current.CancellationToken);
        words.Count.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void The_native_library_loads() => WhisperTranscriber.CheckRuntime(RequireModel()).ShouldBeNull();

    [Fact]
    public void A_model_that_is_not_one_is_reported()
    {
        var notAModel = Path.GetTempFileName();
        try
        {
            WhisperTranscriber.CheckRuntime(notAModel).ShouldNotBeNull();
        }
        finally
        {
            File.Delete(notAModel);
        }
    }
}
