# macOS hang watchdog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A frozen ShowTime must never cost a reboot: the macOS app notices when its main thread stops answering, records why, and offers to force it to quit; the most likely cause of the freeze seen on 2026-11-04 (deleting a video while Whisper transcribes it) is fixed; and the log tells what the app was doing.

**Architecture:** The detection rule is a core class (`HangWatchdog`, tested with `FakeTimeProvider`) that pings the UI thread through `IUiThread` and reports to a port (`IHangResponder`). The Mac adapter answers from a background thread with tools that work while AppKit is frozen: `/usr/bin/sample` for the report, `osascript` for the dialog (a separate process, which also takes the keyboard back), `pkill -INT` to let ffmpeg finish its MP4, then a kill. Whisper's model is freed only once whisper.cpp has stopped. `Transcriptions` and the Mac app log each step of a video's life.

**Tech Stack:** .NET 10, C#, AppKit (`net10.0-macos`), Whisper.net 1.9.1, xunit v3 + Shouldly + NSubstitute, `Microsoft.Extensions.Logging`.

**Spec:** none written apart; this plan's Goal and the diagnosis below.

Diagnosis (2026-11-04, CI build of `main` c9040a9): a video was recorded at 22:10:05, sent to the Trash at 22:10:36 (seconds after it stopped, while its microphone was being transcribed), then ShowTime froze (spinning cursor over its menu, keyboard dead in every application, ShowTime absent from "Forcer à quitter") until a reboot at 22:14. The log stops at "First microphone packet". In Whisper.net 1.9.1, `WhisperProcessor.ProcessAsync` stops waiting as soon as its token is cancelled while whisper.cpp keeps running on its own thread; `WhisperTranscriber`'s `using` then calls `processor.Dispose()` (which throws "Cannot dispose while processing") and `factory.Dispose()`, which frees the model (`Whisper_Free`) under whisper.cpp's feet. `AsyncAutoResetEvent.Set` completes a `TaskCompletionSource` without `RunContinuationsAsynchronously`, so this chain can run on the thread that cancelled: the UI thread (`VideoPreviewWindowController.Discard` → `SubtitleEditor.Close` → `Transcriptions.Cancel`). Not proven (no stack was captured): hence the watchdog's `sample` report.

## Global Constraints

- No network call of any kind (CLAUDE.md): the watchdog only runs local tools from `/usr/bin` and `/usr/sbin`.
- UI strings in French, code and comments in English; analyzers at `latest-recommended`, warnings as errors; `using` directives in `src/ShowTime.Mac` kept by hand, `System` first then alphabetical.
- The core keeps 90 % line coverage (`python build/check-coverage.py … 90`).
- No test is run locally without the user's go: verify through CI (`gh workflow run CI --ref fix/macos-hang-watchdog`, and the PR's own run).
- Thresholds: ping every 2 s, a hang is 10 s without an answer, asked again 30 s after "Attendre". Elapsed time from `TimeProvider.GetTimestamp` (on macOS it does not count while the Mac sleeps).
- The force-quit never happens without the user's click: no automatic kill.
- No version bump (done at release time).

## Review Focus

- The Mac waking from sleep: the first tick after waking must not see the sleep as a hang (monotonic uptime, not wall clock). Covered by using `GetTimestamp`; checked by hand (Task 4 checklist).
- A modal alert (`NSAlert.RunModal`, the save panel) left open for minutes is not a hang: the main dispatch queue is drained in modal and menu-tracking run-loop modes. Checked by hand (Task 4 checklist).
- The dialog must come to the front and take the keyboard while ShowTime is frozen and frontmost. Checked by hand with `--test-hang` (Task 4).
- ShowTime answering again while the dialog is open: the dialog disappears by itself. Test in Task 3 (`Recovered` reported once) and by hand.
- Force-quit during a recording: the MP4 is still readable (ffmpeg got SIGINT and wrote its index). Checked by hand (Task 4).

---

### Task 1: Whisper lets go of the model only once whisper.cpp has stopped

**Files:**
- Modify: `src/ShowTime.Infrastructure/Transcription/WhisperTranscriber.cs`
- Test: `tests/ShowTime.Infrastructure.IntegrationTests/Transcription/WhisperTranscriberTests.cs`

**Interfaces:**
- Produces: no signature change; `TranscribeAsync` now ends in `OperationCanceledException` when cancelled mid-run, never in "Cannot dispose while processing".

- [ ] **Step 1: Write the failing test** `A_transcription_cancelled_while_whisper_runs_ends_cancelled_and_the_next_one_works`: with `RequireModel()`, a `CancellationTokenSource cancel`, call `TranscribeAsync(Speech, model, "fr", _ => _ = cancel.CancelAsync(), cancel.Token)` (whisper.cpp reports progress 0 % at the start of its first window, so the cancellation lands while it runs); `await Should.ThrowAsync<OperationCanceledException>(…)`; then a second `TranscribeAsync` on `Speech` with `TestContext.Current.CancellationToken` returns at least 6 words.
- [ ] **Step 2: Push and let CI run it** (`gh workflow run CI --ref fix/macos-hang-watchdog`). Expected on the macOS and Windows jobs: FAIL (plain `Exception` "Cannot dispose while processing", or the test host dies).
- [ ] **Step 3: Implement** in the `Task.Run` lambda: `await using var processor` (Whisper.net's `DisposeAsync` waits for its processing semaphore, i.e. for whisper.cpp to return); wrap the `await foreach` in `try { … } finally { await Task.Yield(); }` with a comment: a cancellation may resume this code on the thread that cancelled (the UI thread); the rest, freeing the model, runs on the thread pool. The factory's `using` stays and now runs after the processor's.
- [ ] **Step 4: CI green** on the same command; the four existing Whisper tests still pass.
- [ ] **Step 5: Commit** `Free Whisper's model only once whisper.cpp has stopped`.

### Task 2: The log tells a video's life

**Files:**
- Modify: `src/ShowTime.Core/Transcription/Transcriptions.cs` (constructor gains `ILogger<Transcriptions> log` as last parameter)
- Modify: `tests/ShowTime.Core.Tests/Fakes.cs` (add `ListLogger<T>`), `tests/ShowTime.Core.Tests/Transcription/TranscriptionsTests.cs`, `tests/ShowTime.Core.Tests/Transcription/SubtitleEditorTests.cs`, `tests/ShowTime.Windows.Tests/Recording/TestTranscriptions.cs` (pass a logger)
- Modify: `src/ShowTime.Mac/StatusMenu.cs`, `src/ShowTime.Mac/Video/VideoPreviewWindowController.cs`, `src/ShowTime.Mac/AppDelegate.cs`

**Interfaces:**
- Produces: `internal sealed class ListLogger<T> : ILogger<T>` with `List<(LogLevel Level, string Message, Exception? Exception)> Entries`; `Transcriptions(…, IUiThread uiThread, ILogger<Transcriptions> log)`; `VideoPreviewWindowController(…, string? microphoneTrack, ILogger log)`.

- [ ] **Step 1: Write the failing tests** in `TranscriptionsTests` (construct with a `ListLogger<Transcriptions> _log`):
  - `A_transcription_logs_its_start_and_its_end`: complete a job on `a.mp4` with two words → two `Information` entries, the first contains `a.mp4` and `started`, the second `a.mp4` and `done`.
  - `Progress_is_not_logged`: call `_transcriber.Last.OnProgress(0.5)` three times → still one entry (`started`).
  - `A_cancelled_transcription_is_logged`: `Cancel` a running job → last entry `Information`, contains `canceled`.
  - `A_transcription_interrupted_by_a_recording_is_logged`: `_recording.Set(true)` while running → an entry contains `interrupted`.
  - `A_failed_transcription_is_logged_as_a_warning_with_its_exception`: `_transcriber.Last.Result.SetException(new InvalidOperationException("boom"))` → last entry `Warning`, `Exception` is that exception.
- [ ] **Step 2: Run** `dotnet test --project tests/ShowTime.Core.Tests` through CI. Expected: FAIL (constructor has no logger).
- [ ] **Step 3: Implement** in `Transcriptions`: log with the video's file name only (`Path.GetFileName`): `Transcription of {Video} started`, `Transcription of {Video} done: {Count} subtitles in {Seconds:F1} s`, `… canceled`, `… interrupted by a recording; it starts again afterwards`, and `LogWarning(ex, "Transcription of {Video} failed")`. Progress updates log nothing. Follow the logging pattern already accepted by the analyzers (`AppDelegate`, `MacSystemAudio`). The other test constructors pass `NullLogger<Transcriptions>.Instance`.
- [ ] **Step 4: Mac app logs** (no automated test, see CLAUDE.md): `StatusMenu` takes `ILogger<StatusMenu>`: `Recording started` / `Recording stopped` when `_video.IsRecording` changes in `UpdateVideoState`, `Video saved: {Name}` in `OpenVideoPreview`, and passes the logger to `VideoPreviewWindowController`, which logs `Video {Name} sent to the Trash` in `Discard`, a warning when the move fails, and `Video preview of {Name} closed` in `WillClose`. `AppDelegate.WillTerminate` logs `ShowTime quits`.
- [ ] **Step 5: CI green** (core, Windows, macOS jobs; coverage ≥ 90 %).
- [ ] **Step 6: Commit** `Log the life of a video and of its transcription`.

### Task 3: HangWatchdog in the core

**Files:**
- Create: `src/ShowTime.Core/Ports/IHangResponder.cs`, `src/ShowTime.Core/Diagnostics/HangWatchdog.cs`
- Modify: `tests/ShowTime.Core.Tests/Fakes.cs` (add `ManualUiThread`, `FakeHangResponder`)
- Test: `tests/ShowTime.Core.Tests/Diagnostics/HangWatchdogTests.cs`

**Interfaces:**
- Produces:
  - `public interface IHangResponder { void Hung(TimeSpan silence, bool first); void Recovered(TimeSpan silence); }`: `Hung` is called off the UI thread and may block while the user decides; `first` is true for the first report of a hang. `Recovered` is called on the UI thread.
  - `public sealed class HangWatchdog(TimeProvider clock, IUiThread uiThread, IHangResponder responder) : IDisposable` with `public static readonly TimeSpan Interval = 2 s, Threshold = 10 s, AskAgainAfter = 30 s;`, `public void Start()`, `public void Dispose()`.
  - Fakes: `ManualUiThread : IUiThread` (queues; `void RunAll()`), `FakeHangResponder` (`List<(TimeSpan Silence, bool First)> Hangs`, `List<TimeSpan> Recoveries`, `Action? OnHung`).

Rule: a periodic timer every `Interval`. A tick with no ping pending posts one (remembering `GetTimestamp()`); a tick with a ping pending for `Threshold` or more reports `Hung` unless a report is under way (re-entrancy guard) or `AskAgainAfter` has not passed since the last report returned. The ping, run on the UI thread, clears the pending state and, after any report, calls `Recovered` with the silence.

- [ ] **Step 1: Write the failing tests** (`FakeTimeProvider clock`, advanced in 2 s steps):
  - `A_ui_thread_that_answers_is_never_reported`: `ImmediateUiThread`, 60 s → `Hangs` and `Recoveries` empty.
  - `An_answer_before_ten_seconds_reports_nothing`: `ManualUiThread`, advance to 8 s, `RunAll()`, advance to 30 s running the queue each step → nothing reported.
  - `Ten_seconds_without_an_answer_is_reported_once`: ping posted at 2 s; at 10 s `Hangs` is empty; at 12 s `Hangs` is `[(10 s, true)]`; at 40 s still one.
  - `A_hang_still_there_is_reported_again_after_thirty_seconds`: at 42 s, `Hangs[1]` is `(40 s, false)`.
  - `An_answer_after_a_report_is_reported_as_recovered_and_a_new_hang_is_first_again`: report at 12 s, `RunAll()` at 13 s → `Recoveries` `[11 s]`; next silence reported at 24 s with `First == true`.
  - `A_report_still_open_is_not_reported_again`: `OnHung = () => clock.Advance(TimeSpan.FromSeconds(20))` (time passing while the dialog is open) → one entry in `Hangs`.
  - `Disposed_it_posts_and_reports_nothing_more`: `Dispose()` at 4 s, advance to 60 s → `ManualUiThread` holds only the first ping, `Hangs` empty.
- [ ] **Step 2: Run** through CI. Expected: FAIL (types do not exist).
- [ ] **Step 3: Implement** `IHangResponder` and `HangWatchdog` (timer from `clock.CreateTimer`, state under a `Lock`, `Responder.Hung` called outside the lock). Doc comments explain why uptime (`GetTimestamp`) and why the guard.
- [ ] **Step 4: CI green**, coverage ≥ 90 %.
- [ ] **Step 5: Commit** `Add a watchdog that notices a UI thread that stops answering`.

### Task 4: The Mac answers a hang

**Files:**
- Create: `src/ShowTime.Mac/Platform/MacHangResponder.cs`
- Modify: `src/ShowTime.Mac/Composition.cs`, `src/ShowTime.Mac/AppDelegate.cs`, `README.md` (troubleshooting), `docs/test-checklist.md` (macOS, new step), `CLAUDE.md` (commands and architecture lines)

**Interfaces:**
- Consumes: `IHangResponder`, `HangWatchdog` (Task 3); `IAppPaths.LogsFolder`.
- Produces: `internal sealed class MacHangResponder(IAppPaths paths, ILogger<MacHangResponder> log) : IHangResponder`.

- [ ] **Step 1: `MacHangResponder.Hung(silence, first)`**: log a warning `The main thread has not answered for {Seconds:F0} s`; when `first`, run `/usr/bin/sample <pid> 3 -file <LogsFolder>/hang-yyyyMMdd-HHmmss.txt` (wait at most 15 s) and log the file's path; then run `/usr/bin/osascript` with `ArgumentList` (no shell quoting), script lines `on run argv` / `activate` / `display dialog (item 1 of argv) with title "ShowTime" buttons {"Attendre", "Forcer l'arrêt"} default button "Attendre" with icon caution` / `return button returned of result` / `end run`, and the message `ShowTime ne répond plus depuis {n} secondes.\n\n« Forcer l'arrêt » ferme ShowTime ; une vidéo en cours d'enregistrement est arrêtée et gardée.`; keep the `Process` under a lock so `Recovered` can kill it. Output `Forcer l'arrêt` → `ForceQuit()`; anything else (Attendre, killed dialog) returns.
- [ ] **Step 2: `ForceQuit()`**: log `Forced to quit by the user`; `/usr/bin/pkill -INT -P <pid> -x ffmpeg`; poll `/usr/bin/pgrep -P <pid> -x ffmpeg` every 200 ms until it exits non-zero or 5 s pass; `Process.GetCurrentProcess().Kill()` (not `Environment.Exit`, whose handlers could wait for the frozen main thread).
- [ ] **Step 3: `Recovered(silence)`**: log `The main thread answers again after {Seconds:F0} s`; kill the dialog's process if any.
- [ ] **Step 4: Wire it**: `Composition` registers `IHangResponder` → `MacHangResponder` and `HangWatchdog`; `AppDelegate.DidFinishLaunching` calls `Start()` after `StatusMenu`. `--test-hang` (read from `NSProcessInfo.ProcessInfo.Arguments`): 5 s after launch, the main queue sleeps 60 s, to check the watchdog by hand.
- [ ] **Step 5: Docs**: README, troubleshooting: after 10 s without answer a window offers « Forcer l'arrêt »; the report `hang-….txt` is in the log folder, to attach to a report; without it, Moniteur d'activité › ShowTime › Forcer à quitter (ShowTime, a menu bar app, is not in ⌥⌘⎋). Checklist macOS, new lines: `open -a ShowTime --args --test-hang` → after ~15 s the dialog appears in front and typing goes to it; « Attendre » → asked again ~30 s later; when the 60 s are over the dialog closes by itself and the log says so; `hang-*.txt` holds the main thread's stack; « Forcer l'arrêt » during a recording quits and leaves a readable MP4 (`ffmpeg -i`); a `NSAlert` left open 2 min and a Mac put to sleep 1 min raise no dialog; delete a video while « Transcription du micro… » shows: no freeze. CLAUDE.md: `--test-hang` under Commands, `Diagnostics/` (`HangWatchdog`) in the core's list.
- [ ] **Step 6: CI green** on all jobs; the `ShowTime-macos` artifact builds.
- [ ] **Step 7: Commit** `Offer to force ShowTime to quit when it stops answering on a Mac`.

### Finish

- [ ] Push the branch, open the PR (body: the diagnosis, the three changes, the manual checks left to do with the `ShowTime-macos` artifact). Do not merge: the user checks it on a Mac first.
