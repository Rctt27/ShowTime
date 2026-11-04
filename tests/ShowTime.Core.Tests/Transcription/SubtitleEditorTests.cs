using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Core.Tests.Transcription;

/// <summary>The subtitle panel's rules, without its views.</summary>
public sealed class SubtitleEditorTests : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ShowTimeEditor");
    private static readonly string Models = Path.Combine(Root, "models");
    private static readonly string Video = Path.Combine(Root, "video.mp4");
    private static readonly string Srt = SubtitleFile.PathFor(Video);

    private readonly MockFileSystem _fs = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeTranscriber _transcriber = new();
    private readonly SubtitleFile _file;
    private readonly Transcriptions _transcriptions;
    private readonly SubtitleEditor _editor;

    public SubtitleEditorTests()
    {
        _fs.AddFile(Path.Combine(Models, "ggml-small-q5_1.bin"), new MockFileData(""));
        _fs.AddFile(Video, new MockFileData(""));
        var settings = new AppSettings(SettingsDefaults.ForWindows(Root));
        var models = new TranscriptionModelLocator(_fs, [Models]);
        var paths = Substitute.For<IAppPaths>();
        paths.WorkFolder.Returns(Path.Combine(Root, "work"));
        _file = new SubtitleFile(_fs);
        _transcriptions = new Transcriptions(_transcriber, new FakeAudioExtractor(_fs), new MicrophoneTracks(settings, models, paths, _fs),
            models, settings, _file, _fs, new FakeRecordingStatus(), new ImmediateUiThread(),
            NullLogger<Transcriptions>.Instance);
        _editor = new SubtitleEditor(_file, _transcriptions, _clock, new ImmediateUiThread());
    }

    public void Dispose() => _transcriptions.Dispose();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static SubtitleCue Cue(double start, double end, string text) => new(S(start), S(end), text);

    /// <summary>"Un" from 1 to 3 s and "Deux" from 5 to 8 s of a 10 s video, as the Windows panel's tests.</summary>
    private void LoadSaved()
    {
        _file.Save(Video, [Cue(1, 3, "Un"), Cue(5, 8, "Deux")]);
        _editor.Load(Video, null);
        _editor.SetDuration(S(10));
    }

    private string? SavedText(int index) => _file.Load(Video)?.Cues[index].Text;

    [Fact]
    public void Saved_subtitles_load_with_the_gaps_between_them()
    {
        LoadSaved();

        _editor.Timeline.ShouldBe([
            new GapEntry(S(0), S(1)), new CueEntry(0, Cue(1, 3, "Un")), new GapEntry(S(3), S(5)),
            new CueEntry(1, Cue(5, 8, "Deux")), new GapEntry(S(8), S(10))
        ]);
        _editor.Status.Text.ShouldBe("2 sous-titres");
        _editor.HasSubtitles.ShouldBeTrue();
        _editor.CanGenerate.ShouldBeFalse();
    }

    [Fact]
    public void One_cue_is_counted_in_the_singular()
    {
        _file.Save(Video, [Cue(1, 3, "Un")]);
        _editor.Load(Video, null);

        _editor.Status.Text.ShouldBe("1 sous-titre");
    }

    [Fact]
    public void A_video_without_subtitles_offers_to_generate()
    {
        _editor.Load(Video, null);

        _editor.HasSubtitles.ShouldBeFalse();
        _editor.CanGenerate.ShouldBeTrue();
        _editor.Timeline.ShouldBeEmpty();
    }

    [Fact]
    public void Unreadable_subtitles_leave_the_video_without_them()
    {
        // Held by another program.
        _fs.AddFile(Srt, new MockFileData("") { AllowedFileShare = FileShare.None });

        _editor.Load(Video, null);

        _editor.HasSubtitles.ShouldBeFalse();
    }

    [Fact]
    public void The_position_picks_its_cue_or_gap()
    {
        LoadSaved();
        var shown = new List<string?>();
        _editor.CueShown += shown.Add;

        _editor.ShowPosition(S(2));
        _editor.ActiveIndex.ShouldBe(1);
        _editor.CueText.ShouldBe("Un");

        _editor.ShowPosition(S(2.5));
        _editor.ShowPosition(S(4));
        _editor.ActiveIndex.ShouldBe(2);
        _editor.CueText.ShouldBeNull();

        shown.ShouldBe(["Un", null]);
    }

    [Fact]
    public void A_new_active_entry_asks_to_be_centred_unless_the_rows_changed_under_an_edit()
    {
        LoadSaved();
        var centred = new List<bool>();
        _editor.ActiveChanged += centred.Add;

        _editor.ShowPosition(S(2));
        _editor.CommitEnd(0, "00:03.500");

        centred.ShouldBe([true, false]);
    }

    [Fact]
    public void An_edit_is_saved_half_a_second_later()
    {
        LoadSaved();

        _editor.CommitText(1, "Deux,\r\ntrois").ShouldBeTrue();

        _clock.Advance(TimeSpan.FromMilliseconds(499));
        SavedText(1).ShouldBe("Deux");
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        SavedText(1).ShouldBe("Deux,\ntrois");
    }

    [Fact]
    public void Each_edit_puts_the_save_off_again()
    {
        LoadSaved();

        _editor.CommitText(0, "Un !");
        _clock.Advance(TimeSpan.FromMilliseconds(400));
        _editor.CommitText(1, "Deux !");
        _clock.Advance(TimeSpan.FromMilliseconds(400));

        SavedText(0).ShouldBe("Un");
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        SavedText(0).ShouldBe("Un !");
    }

    [Fact]
    public void Closing_saves_a_pending_change()
    {
        LoadSaved();
        _editor.CommitText(1, "Deux, trois");

        _editor.Close(discard: false);

        SavedText(1).ShouldBe("Deux, trois");
    }

    [Fact]
    public void Discarding_cancels_the_transcription_and_saves_nothing()
    {
        var job = _transcriptions.Enqueue(Video);
        _editor.Load(Video, job);

        _editor.Close(discard: true);
        _transcriber.Last.Token.IsCancellationRequested.ShouldBeTrue();
        job.State.ShouldBe(TranscriptionState.Canceled);
        _fs.File.Exists(Srt).ShouldBeFalse();
    }

    [Fact]
    public void Discarding_drops_a_pending_save()
    {
        LoadSaved();
        _editor.CommitText(1, "Deux, trois");

        _editor.Close(discard: true);
        _clock.Advance(TimeSpan.FromSeconds(1));

        SavedText(1).ShouldBe("Deux");
    }

    [Fact]
    public void A_refused_time_returns_the_document_message_and_changes_nothing()
    {
        LoadSaved();

        var error = _editor.CommitEnd(0, "00:06.000");

        error.ShouldNotBeNull();
        error.ShouldBe(new SubtitleDocument([Cue(1, 3, "Un"), Cue(5, 8, "Deux")]).SetEnd(0, S(6)));
        _editor.Cues[0].ShouldBe(Cue(1, 3, "Un"));
    }

    [Fact]
    public void An_unreadable_time_is_explained()
    {
        LoadSaved();

        _editor.CommitStart(0, "abc").ShouldBe("Temps illisible : écrivez-le par exemple 01:02.345.");
        _editor.Cues[0].ShouldBe(Cue(1, 3, "Un"));
    }

    [Fact]
    public void An_accepted_time_moves_the_cue_and_opens_a_gap()
    {
        LoadSaved();
        var changes = 0;
        _editor.TimelineChanged += _ => changes++;

        _editor.CommitEnd(0, "00:02.000").ShouldBeNull();

        _editor.Cues[0].ShouldBe(Cue(1, 2, "Un"));
        _editor.Timeline[2].ShouldBe(new GapEntry(S(2), S(5)));
        changes.ShouldBe(1);
    }

    [Fact]
    public void The_same_time_written_otherwise_changes_nothing()
    {
        LoadSaved();
        var changes = 0;
        _editor.TimelineChanged += _ => changes++;

        _editor.CommitStart(0, "0:01").ShouldBeNull();

        changes.ShouldBe(0);
    }

    [Fact]
    public void A_cue_added_in_a_gap_starts_there_and_a_removed_cue_leaves_one()
    {
        LoadSaved();

        var index = _editor.AddInGap((GapEntry)_editor.Timeline[2]);
        index.ShouldBe(1);
        _editor.Cues[1].Start.ShouldBe(S(3));

        _editor.Remove(1);
        _editor.Cues.Count.ShouldBe(2);
    }

    [Fact]
    public void The_transcription_progress_and_result_show()
    {
        var job = _transcriptions.Enqueue(Video);
        _editor.Load(Video, job);
        _editor.SetDuration(S(10));
        var recentred = new List<bool>();
        _editor.TimelineChanged += recentred.Add;

        _transcriber.Last.OnProgress(0.42);
        _editor.Status.ShowsProgress.ShouldBeTrue();
        _editor.Status.Progress.ShouldBe(0.42);
        _editor.Status.Text.ShouldStartWith("Transcription du micro… ");
        _editor.HasSubtitles.ShouldBeTrue();
        _editor.CanGenerate.ShouldBeFalse();

        _transcriber.Last.Complete(new TranscribedWord(S(0.5), S(2), " Bonjour"));

        _editor.Status.ShowsProgress.ShouldBeFalse();
        _editor.Cues.ShouldBe([Cue(0.5, 2.5, "Bonjour")]);
        _editor.Status.Text.ShouldBe("1 sous-titre");
        recentred.ShouldContain(true);
    }

    [Fact]
    public void A_waiting_transcription_says_why()
    {
        var first = _transcriptions.Enqueue(Video);
        var second = _transcriptions.Enqueue(Video);
        _editor.Load(Video, second);

        _editor.Status.Text.ShouldBe("Transcription en attente : elle commence après la transcription ou l'enregistrement en cours.");
        first.State.ShouldBe(TranscriptionState.Running);
    }

    [Fact]
    public void A_failed_transcription_offers_retry()
    {
        var job = _transcriptions.Enqueue(Video);
        _editor.Load(Video, job);

        _transcriber.Last.Result.SetException(new InvalidOperationException("boom"));

        _editor.Status.CanRetry.ShouldBeTrue();
        _editor.Status.Text.ShouldBe(job.Error);
        _editor.CanGenerate.ShouldBeFalse();

        _editor.Retry();
        _editor.Status.CanRetry.ShouldBeFalse();
        _transcriber.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public void Nothing_heard_says_how_to_add_subtitles_and_offers_to_generate_again()
    {
        var job = _transcriptions.Enqueue(Video);
        _editor.Load(Video, job);

        _transcriber.Last.Complete();

        _editor.Status.Text.ShouldBe("Aucune parole détectée. Ajoutez des sous-titres avec « Ajouter » dans la liste.");
        _editor.CanGenerate.ShouldBeTrue();
    }

    [Fact]
    public void Generating_again_replaces_subtitles_without_any_cue()
    {
        _editor.Load(Video, _transcriptions.Enqueue(Video));
        _transcriber.Last.Complete();

        _editor.Generate(null);
        _editor.Status.Text.ShouldStartWith("Transcription du micro… ");

        _transcriber.Last.Complete(new TranscribedWord(S(0.5), S(2), " Bonjour"));
        _editor.Cues.ShouldBe([Cue(0.5, 2.5, "Bonjour")]);
    }

    [Fact]
    public void Undo_and_redo_go_through_the_document()
    {
        LoadSaved();
        _editor.CommitText(0, "Un !");

        _editor.Undo().ShouldBeTrue();
        _editor.Cues[0].Text.ShouldBe("Un");
        _editor.Redo().ShouldBeTrue();
        _editor.Cues[0].Text.ShouldBe("Un !");
    }

    [Fact]
    public void Undo_without_subtitles_does_nothing()
    {
        _editor.Load(Video, null);

        _editor.CanUndo.ShouldBeFalse();
        _editor.Undo().ShouldBeFalse();
        _editor.Redo().ShouldBeFalse();
    }

    /// <summary>For the menu's Annuler and Rétablir, enabled only when they would do something.</summary>
    [Fact]
    public void Undo_and_redo_say_when_they_can_act()
    {
        LoadSaved();
        _editor.CanUndo.ShouldBeFalse();

        _editor.CommitText(0, "Un !");
        _editor.CanUndo.ShouldBeTrue();
        _editor.CanRedo.ShouldBeFalse();

        _editor.Undo();
        _editor.CanRedo.ShouldBeTrue();
    }

    [Fact]
    public void A_save_that_fails_says_so_under_the_count()
    {
        LoadSaved();
        // The temporary file cannot be written where a folder already is.
        _fs.AddDirectory(Srt + ".tmp");

        _editor.CommitText(0, "Un !");
        _clock.Advance(SubtitleEditor.SaveDelay);

        _editor.Status.IsError.ShouldBeTrue();
        _editor.Status.Text.ShouldStartWith("2 sous-titres\nLes sous-titres n'ont pas pu être enregistrés : ");
    }
}
