# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ShowTime is a tray (Windows) and menu bar (macOS) screen-capture and screen-recording tool that works without any account or remote service. **Captures must never leave the machine**: no network calls, no upload, no telemetry. This is also why the default Windows output folder avoids a OneDrive-synced Pictures folder (`AppPaths.WindowsOutputFolder`), why ffmpeg is never downloaded by the app (it ships with it, pinned and checked by SHA-256 in `build/fetch-ffmpeg.sh`), why speech is transcribed on the machine with a Whisper model shipped in the release (never downloaded by the app), and why the log is a local file only.

UI strings are French; code and comments are English.

ShowTime was named LocalTake up to v0.4: its settings are not carried over. The name stays on purpose in the macOS signing certificate (`identity=LocalTake` in `build/package-macos.sh`; renaming it means new secrets) and in `tests/fixtures/legacy`.

## Before starting work

ShowTime is developed from several machines, so a local clone can be far behind GitHub. Before writing a plan, branching or changing code: `git fetch`, compare `main` with `origin/main` and pull if it is behind, then check that the SDK pinned in `global.json` is installed (`dotnet --list-sdks`). Work started on a stale `main` has to be redone on the new code.

Branch from `main`, merge through a pull request with a merge commit (`gh pr merge --merge`) once CI is green, then delete the branch, locally and on GitHub.

## Commands

Requires the .NET 10 SDK (pinned in `global.json`). Analyzers run at `latest-recommended` with warnings as errors; `.editorconfig` holds the style, and CI rejects unformatted code.

```
dotnet build ShowTime.slnx -c Release
dotnet test --solution ShowTime.slnx                 # every test project (Microsoft.Testing.Platform, xunit v3)
dotnet test --project tests/ShowTime.Core.Tests      # one project
dotnet test --project tests/ShowTime.Core.Tests --coverage --coverage-output-format cobertura --coverage-output core.cobertura.xml
python build/check-coverage.py TestResults/core.cobertura.xml 90
dotnet run --project src/ShowTime.Windows            # the app; add "-- --diagnostics" for the self-check report
dotnet format ShowTime.slnx                          # apply the style
dotnet publish src/ShowTime.Windows -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The macOS app builds on a Mac only, with Xcode and the `macos` workload; `ShowTime.Mac.slnx` holds it with the projects it uses. Each .NET for macOS requires one Xcode: CI pins workload set `10.0.401` (.NET for macOS 26.5) with Xcode 26.6, in `ci.yml` and `release.yml` alike; change both together. The app is trimmed (`TrimMode` partial): it keeps only what it uses of .NET's libraries and Apple's bindings, and links only the Apple frameworks those need. A trim warning fails its build, and the trim analyzer runs on the core, the infrastructure and `ShowTime.Platform.MacOS` on every OS, so reflection on .NET's types (JSON without generated metadata, for one) is caught on Windows too.

On Windows, `dotnet format ShowTime.slnx` does not cover `src/ShowTime.Mac` (CI checks `ShowTime.Mac.slnx` on macOS): keep its style by hand, `using` directives with `System` first, then alphabetical.

```
dotnet workload install macos --version 10.0.401
dotnet build src/ShowTime.Mac                        # bin/Release/net10.0-macos/ShowTime.app, universal (arm64 and x86_64)
bash build/package-macos.sh <ShowTime.app> <zip>  # adds ffmpeg, whisper.cpp and the model, signs, zips; only a packaged app transcribes
open -a ShowTime --args --diagnostics             # report in ~/Library/Logs/ShowTime/diagnostics.txt
```

Run from a shell (`Contents/MacOS/ShowTime`), the app would be checked with the shell's privacy permissions, not its own.

Package versions are managed centrally in `Directory.Packages.props`: a `PackageReference` in a project carries no version.

Only one instance can run (a named mutex on Windows, the bundle identifier on macOS): kill `ShowTime` before running again, otherwise a new launch exits immediately. Global shortcuts fail to register while another capture tool holds them.

The UI on a real screen is verified by hand with `docs/test-checklist.md`. To check a recording, `ffmpeg -i file.mp4` lists its streams and `-af volumedetect -f null -` measures its audio. Settings and log locations are in README.md.

## Tests

- `tests/ShowTime.Core.Tests`: unit tests of the core, without I/O (MockFileSystem, FakeTimeProvider, fakes in `Fakes.cs`). CI requires 90 % line coverage of the core.
- `tests/ShowTime.Infrastructure.IntegrationTests`: real files and a real ffmpeg, which records its own test sources (`lavfi`) with the production encoding. Tests needing ffmpeg skip themselves when it is not installed; CI installs it. The transcription tests read `tests/fixtures/speech` with the Whisper model in `SHOWTIME_WHISPER_MODEL` (CI downloads `ggml-tiny.bin`), or the one ShowTime would find, and skip without one.
- `tests/ShowTime.Platform.Tests`: each platform's adapters, run on that platform (CI runs Windows and macOS); tables, files and the macOS screen geometry (`MacScreenSpace`) are checked everywhere.
- The macOS app has no automated tests (no reliable `net10.0-macos` test host in CI): logic worth a test goes to the core or `ShowTime.Platform.MacOS`, and the app is checked by hand with the macOS part of `docs/test-checklist.md`, using the CI artifact `ShowTime-macos`.
- `tests/ShowTime.Windows.Tests` (Windows only): the WPF adapters and windows, off screen on one STA thread hosting `App` without starting it (`Sta.Run`); the editor's export is checked pixel by pixel. Tests that record the screen with ffmpeg share the non-parallel `ScreenRecordings` collection, so that one's windows do not end up in the other's video.
- `tests/fixtures/legacy`: the behaviour of v0.1.0 (file names, shortcuts, ffmpeg command lines, settings files), recorded from the original code. Characterization tests compare against it; never regenerate it, and make a deliberate change in the test with a comment.
- Run no test locally without the user's go: `ShowTime.Windows.Tests` shows coloured windows and `Platform.Tests` captures sound, which crashes other applications. Verify through CI: it runs on pull requests and on `main`; for a branch, `gh workflow run CI --ref <branch>`. `MacAudioPipeTests` (dispose under 1 s) can fail on a slow runner: `gh run rerun <id> --failed`.
- New behaviour starts with a failing test.

## Versioning and releases

[Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`. The only source of the version is `<Version>` in `Directory.Build.props`.

- While the major version is 0 (initial development), MINOR is for new features and behaviour changes, including breaking ones; PATCH is for fixes only. 1.0.0 is decided by the user.
- What counts as the public interface: the shortcuts, the settings in `settings.json` (an older file must keep loading) and the file names produced.
- To release: bump `<Version>`, commit `Release vX.Y.Z`, tag `vX.Y.Z` on `main` and push both. `.github/workflows/release.yml` builds the Windows zip (`build/package-windows.sh`, also run by CI: `ShowTime.exe` and `LISEZ-MOI.txt` alone at the root, so that the exe is easy to find, and in `app` (`AppPaths.WindowsShippedFolder`) `ffmpeg.exe`, Whisper's `runtimes\win-x64`, the model in `models`, pinned by revision and SHA-256, and the licences; `build/check-windows-package.ps1` runs its `--diagnostics` and requires ffmpeg and the transcription from `app`) and the macOS app (`build/package-macos.sh`: ffmpeg in `Contents/MacOS`, whisper.cpp's libraries in `Contents/MonoBundle/runtimes/macos-<arch>` copied from the restored package, the model in `Contents/Resources/models` (`build/fetch-whisper-model.sh`, which the Windows zip uses too), signed with the self-signed `LocalTake` certificate (named after the app's former name) from the `MACOS_SIGNING_P12` / `MACOS_SIGNING_PASSWORD` secrets so that macOS keeps its permissions across versions, ad hoc without them; see `docs/macos-signing.md`; not notarized), both with `THIRD-PARTY-NOTICES.md` and `licenses/`, and creates the GitHub release, whose notes give ffmpeg's source (GPL); it refuses a tag that does not match `<Version>`.

## Architecture

A shared core, and one native app per operating system. Keep every rule that can be tested in the core, so that each app stays views and adapters.

- `src/ShowTime.Core` (`net10.0`, no UI framework, no OS API): the rules and use cases. `Capture/` (`CaptureWorkflow`, `CaptureDelivery`, `SelectionModel`), `Recording/` (`RecordingSession`, `RecordingCommand`, `FfmpegLocator`, and the ffmpeg inputs of each OS: `DirectShowGdigrab`, `AvFoundation`), `Files/` (`FileNamePattern`, `OutputPathAllocator`, `FileCounter`, `ImageSaver`), `Editor/` (`EditorGeometry`, `UndoHistory`), `Hotkeys/` (`Hotkey`, `KeyCode`), `Settings/` (`AppSettings`, `SettingsDefaults`, `SettingsEditor`), `Transcription/` (`SubtitleDocument`, `SrtFormat`, `Timecode`, `SubtitleFile`, `Transcriptions`, `MicrophoneTracks`, `TranscriptionModelLocator`). Everything platform-specific is an interface in `Ports/`; images cross ports as opaque `IImage` or as `PixelBuffer` (BGRA, straight alpha), which also crops and pixelates.
- `src/ShowTime.Infrastructure` (portable): `JsonSettingsStore`, the ffmpeg processes (`FfmpegVideoRecorder`, `FfmpegAudioDeviceLister`), `WhisperTranscriber` (Whisper.net), `FileLoggerProvider`, `AppPaths`, and `DiagnosticsReport` (`--diagnostics`).
- `src/ShowTime.Platform.Windows`: the Win32 ports in `LibraryImport` (shortcuts, screen grab and window list, clipboard, Recycle Bin, Run key, Explorer, window display affinity). `src/ShowTime.Platform.MacOS` (`net10.0`, tested on every runner): what Apple's bindings do not cover, Carbon shortcuts and key codes, the window list, and the screens' geometry (`MacScreenSpace`).
- `src/ShowTime.Windows` (WPF, `net10.0-windows`, `AssemblyName` ShowTime): the views and the WPF adapters (`Adapters/`). `App.ConfigureServices` is the composition root. WinForms is used for `NotifyIcon` and for the overlay and frame windows.
- `src/ShowTime.Mac` (`net10.0-macos`, AppKit and AVKit, `LSUIElement`: no Dock icon): `StatusMenu` (the menu bar icon, as `TrayController`), `Capture/` (one overlay window per screen), `Editor/`, `Recording/`, `Video/` (an `AVPlayerView` with ShowTime's keys, and `TranscriptPanelView`), `Settings/`, and the adapters on Apple's APIs (`Platform/`: ScreenCaptureKit, NSPasteboard, Trash, `SMAppService`; `Adapters/`). `Composition.Build` is the composition root. A main menu, invisible in a menu bar app, gives the editor ⌘S, ⌘Z, ⌘C and ⌘Q through the responder chain.

Flow: `App` → `TrayController` (tray menu, global shortcuts) → `CaptureWorkflow` or `RecordingSession`. A capture freezes the virtual desktop (`IScreenGrabber`), shows `SelectionOverlay`, then crops the frozen pixels; `CaptureDelivery` applies `AfterCaptureAction` (open the editor, save, copy, or save and copy).

### Pixel and DPI model (read before touching capture or the editor)

- Every screen coordinate is in **physical pixels** (`PixelRect`): virtual desktop, monitors, windows, overlay input, saved region. The process is Per-Monitor-V2 aware (`app.manifest`).
- `SelectionOverlay` is one borderless form over the whole virtual desktop, drawing the frozen screenshot; its rules are `SelectionModel`'s.
- The editor canvas is sized in image pixels (bitmaps at 96 DPI, so one WPF unit is one pixel). Export (`EditorWindow.RenderImage`) renders the canvas through a `VisualBrush` at exactly the image's pixel size, independent of zoom and scroll. Sizes chosen in the toolbar (stroke, font, badge, blur block) are multiplied by the monitor's DPI scale; zoom is shown relative to screen pixels.

### macOS screens

- macOS works in points, with Cocoa's origin at the bottom left of the main screen; screens may have different scales. `MacScreenSpace.Layout` lays them out in pixels, origin at the top left of the main screen: origins scaled by the largest scale, sizes by each screen's own, so each keeps its full resolution and none overlap (gaps between them stay black in a capture). `ToPixel`, `ToCocoa` and `FromCocoa` convert; `FromTopLeftPoints` converts Quartz window frames.
- A capture takes one ScreenCaptureKit screenshot per screen and assembles them (`MacScreenSpace.Compose`). Without the Screen Recording permission it throws a message saying where to grant it. The window list comes from `CGWindowListCopyWindowInfo`, the only one ordered front to back.
- A selection stays on the screen where it started. ffmpeg numbers screens in `CGGetActiveDisplayList`'s order, which `MacDisplays` (`IDisplays`, printed by `--diagnostics`) follows; it need not be `NSScreen.Screens`'.
- Shortcuts are registered exclusively, and those macOS uses itself (`CopySymbolicHotKeys`, Cmd+Shift+3…) are refused, so a taken shortcut is reported rather than silent.

### Editor (`EditorWindow`)

- Annotations are plain WPF elements on the `Surface` canvas (the first child is the capture). Each carries a `TranslateTransform`; moving, cropping and undo change that offset. Shapes and sizes come from `EditorGeometry`.
- Crop does not flatten: it swaps the base image and shifts every annotation, so annotations stay editable and undo restores both.
- Undo/redo is an `UndoHistory` of `(Undo, Redo)` closures. Keyboard shortcuts are ignored during a gesture; Escape or a lost mouse capture cancels it.
- Blur is `PixelBuffer.Pixelate` of the base image, placed as an image annotation.
- A fresh capture counts as unsaved; closing asks to save.

### Shortcuts

- `Hotkey` (core) parses and formats strings like `Ctrl+Alt+PrintScreen` with a framework-neutral `KeyCode`. A key without modifiers is only allowed for Print Screen and the F keys, since a plain key registered system-wide would break it in every other application. `Format(HotkeyNaming.MacOS)` writes Option and Cmd; parsing accepts both namings.
- Windows: `RegisterHotKey` on a message-only window (`WindowsHotkeys`). For the macOS app: Carbon `RegisterEventHotKey` (`MacHotkeys`), which needs no Accessibility permission and reports the release too; a Mac has no Print Screen, so `SettingsDefaults.ForMacOS` uses Ctrl+Shift with digits (Cmd+Shift+3/4/5 belong to macOS), and Option+Space for the highlight (Ctrl+Space selects the input source; a test checks that macOS accepts it).
- Actions run through `IUiThread.Post`, outside the input handling, because captures open windows. Shortcuts are unregistered while the settings window is open, so pressing one reaches the input box. Print Screen only produces a key-up event on Windows, which the box handles separately.

### Video (`RecordingSession`, `Recording/`)

- Recording shells out to ffmpeg: the setting, then the one shipped with ShowTime (in the zip's `app` folder, `AppPaths.WindowsShippedFolder`; `Contents/MacOS` in the app bundle, `AppPaths.FfmpegDirectory`), then PATH, then Homebrew's folders for a development build on macOS (an app started from the Finder does not get the shell's PATH). The shipped one is ffmpeg 9.0.2, fetched by `build/fetch-ffmpeg.sh` (gyan.dev essentials on Windows; Martin Riedl's arm64 and amd64 builds merged with `lipo` on macOS), which also checks libx264 and the capture inputs; the CI tests run against it.
- `IFfmpegPlatform` gives each OS its inputs; `RecordingCommand` adds the mapping and the encoding, common to all. Windows (`DirectShowGdigrab`): `gdigrab` takes physical virtual-desktop coordinates, DirectShow the microphone, and a named pipe the computer sound. macOS (`AvFoundation`): a whole display is recorded and cropped to the area (`-vf crop`); an area across two displays keeps the part on the display holding most of it. Width and height are even for H.264/yuv420p (`PixelRect.WithEvenSize`).
- Nothing said while the counter runs may be cut, and the recording starts when the countdown reaches 0. ffmpeg takes a while to open its devices (about 0.3 to 0.8 s), so `RecordingSession` launches it that long before 0, as long as it took the previous time (0.7 s at first), and starts the counter as soon as ffmpeg prints "Press [q] to stop" (read through `-loglevel level+info`), even slightly before 0. If it is slower than that, the bar shows "Démarrage…" after 0 until then. Stop during the countdown discards the ffmpeg already launched. End: x264 runs with `-tune zerolatency`; with frame threads it held about 0.35 s of frames and `q` discarded the matching audio. The integration test `Stop_keeps_the_end_of_the_sound` guards this.
- Stop sends `q` on ffmpeg's stdin, which writes the MP4 index. Killing the process leaves an unplayable file, so kill is only for cancel and failure, and the file is then deleted.
- On the bar, the status is the on/off button (`StatusButton`, `RecordingBar.SetMode`): ▶ with the Start shortcut when ready, nothing during the countdown, ■ with the elapsed time and the Stop shortcut from `ShowStarting` or `ShowRecording`, nothing while finishing. There is no separate Start or Stop button. `ShowRecording` usually comes straight from the countdown (ffmpeg captures before 0), so it must switch the bar to stopping by itself.
- The recording frame (`RecordingFrame`) is a click-through layered window drawn just outside the area; it and the bar are excluded from captures (`WDA_EXCLUDEFROMCAPTURE`), so neither can appear in the video.
- Highlight (`Highlighter`, core): while `RecordingSession` is in Recording, holding `HighlightTrigger` (`Ctrl+Space` by default; any key alone, or `XButton1`/`XButton2`, since it is only registered then) draws a yellow trail behind the mouse; on release it stays 300 ms, then fades out in 500 ms (`HighlightTrail.FadeOpacity`). The frames run on the `TimeProvider`. `IHighlightInput` on Windows (`WindowsHighlightInput`): `RegisterHotKey` for the press, `GetAsyncKeyState` for the hold (key and modifiers), or a `WH_MOUSE_LL` hook that swallows the side button. On macOS (`MacHighlightInput`): Carbon's press and release, the modifiers from `NSEvent.CurrentModifierFlags`, the mouse converted by `MacScreenSpace.PointToPixel`; keyboard only, side buttons needing the Accessibility permission; the overlay is `MacHighlightOverlay`, a transparent `NSWindow` left capturable. The Windows overlay (`HighlightOverlay`) is a click-through layered window like the frame but deliberately not excluded from capture, so gdigrab records it; the stroke is drawn without antialiasing on a magenta color key, and the window's opacity makes it translucent and fades it without repainting.
- `RecordingSession` is a state machine (Idle, Selecting, Ready, Countdown, Recording, Finishing) whose countdown runs on a `TimeProvider`. After the selection, the bar waits in Ready. Image captures are refused while it is busy (`IRecordingStatus`). The Start and Stop shortcuts are registered on a separate `IGlobalHotkeys` only from Ready until the session ends; Stop during the countdown returns to Ready.
- A saved video opens in `VideoPreviewWindow` and plays there (`MediaElement`), with a frame preview on hover over the seek bar from a second, paused `MediaElement`; ← and → jump 5 seconds (`PlayerSeek`, both apps). Both release the file before "Supprimer" sends it to the Recycle Bin. "Refaire" sends the video to the trash and records again the same way (`VideoSavedEventArgs.FullScreen`); it is refused while another session is under way.
- "Computer sound": ShowTime captures it itself on both platforms and writes raw PCM to a pipe that ffmpeg reads (`PipedAudio`), opened after the screen. On macOS, `MacSystemAudio` (the app) starts ScreenCaptureKit (audio only, 48 kHz stereo float, its own sounds excluded) when ffmpeg opens the FIFO of `MacAudioPipe` (`Platform.MacOS`, `mkfifo`); each packet is dated on the host clock and placed by `AudioTimeline` (core), which fills gaps and idle time with silence (up to `Slack`, 250 ms, behind the clock) and trims what silence already covered, so the track lasts as long as the video; a capture that fails leaves a silent track. BlackHole is no longer read, only kept out of the microphones (`IFfmpegPlatform.SystemAudioDevice`); the sync is checked by hand with `build/sync-check.sh`, since the CI cannot record a Mac's screen. On Windows: `WasapiSystemAudio` (`Platform.Windows`, NAudio) records the default output in WASAPI loopback and writes raw PCM to a named pipe, which ffmpeg reads (`PipedAudio`). `ISystemAudioSource` is the port; `FfmpegVideoRecorderFactory` starts the capture before ffmpeg and `FfmpegVideoRecorder` owns it. A virtual audio driver some capture tools install (`virtual-audio-capturer`) is not used, only kept out of the microphones. `IFfmpegPlatform.CanRecordSystemAudio` says whether the sound can be recorded. With a microphone too, the two are mixed with `amix`.
- Input order sets the sound/picture offset, because ffmpeg starts each input's timestamps at 0: microphone, screen, then the pipe (`CaptureInputs.TrailingAudioInputs`). The capture only starts when ffmpeg opens the pipe, and gdigrab and the pipe get `-probesize 32 -analyzeduration 0`. With the pipe before the screen, or gdigrab fully probed (about 20 frames), the sound was 0.6 to 0.8 s off. `SyncTests` (Windows tests) measures it with a flash and a beep (within ±0.1 s).
- Loopback sends nothing while nothing plays, so the capture also plays silence on that output. If the output is lost (headset unplugged) or the capture cannot start, it writes zeros in real time, so the track keeps its length and `amix` its levels. NAudio's objects are built on a pool thread, so that their events do not wait for the UI thread.
- Stop sends `q` and frees the capture once ffmpeg has exited; if ffmpeg is still running 3 s later, the pipe is closed: ffmpeg waits for its input threads, and one reading an empty pipe never returns.
- The bar's two toggles write `RecordMicrophone` and `RecordSystemAudio` directly to the settings, so the last choice is remembered. An empty `Microphone` means the first one ffmpeg lists.

### Transcription (`Transcription/`, `SubtitleEditor`, `TranscriptPanel`, `TranscriptPanelView`)

- Two steps: record, then transcribe. With a microphone and a model, the same ffmpeg writes a second output: the microphone alone (always `AudioInputs[0]`), 16 kHz mono PCM, in `IAppPaths.WorkFolder` (`MicrophoneTracks`). Same input, so its times are the video's (`The_microphone_track_is_a_16_kHz_mono_wav_in_time_with_the_video`); the MP4 itself is unchanged. The mixed sound of the MP4 is not transcribed when the track exists: it holds the computer sound too. The bar's "Sous-titres" toggle (`TranscribeMicrophone`, captured at the start in `VideoSavedEventArgs.Transcribe`) only decides when: straight away, or from the player's "Générer les sous-titres" button, shown while `SubtitleEditor.CanGenerate` (no cue, no transcription under way). The player keeps the track until then and deletes it on closing (`Transcriptions.DiscardTrack`); without a track, `Transcriptions` first extracts the video's sound (`IAudioExtractor`, `FfmpegAudioExtractor`, `AudioExtraction`).
- `Transcriptions` (core) runs one `ITranscriber` at a time, writes the `.srt` next to the video (`SubtitleFile`, UTF-8 with BOM for Windows players, none when nothing was said) and deletes the track. Whisper takes the processor: nothing is transcribed while `IRecordingStatus.IsRecording`, and a transcription under way when a recording starts is cancelled and begins again afterwards. A failure keeps the track for "Réessayer"; leftover tracks are deleted at startup.
- `WhisperTranscriber`: Whisper.net, half the cores, word by word (`WithMaxSegmentLength(1)` + `SplitOnWord`: each segment is one word with its punctuation and times). Its native libraries live in `runtimes\<rid>` (a single-file exe cannot hold them): in the zip's `app` folder, which `WhisperTranscriber.LoadNativeLibrariesFrom` makes Whisper.net search first (`RuntimeOptions.LibraryPath`), else next to the exe for a development build; the Windows publish keeps only its own (`KeepOnlyThisPlatformsWhisperRuntime`). The macOS app takes none from the build (`WhisperRuntimes=false`): built into the app, they stopped it at launch, one no longer finding another by its install name; `package-macos.sh` copies them as they are, and Whisper.net loads them in order from `MonoBundle/runtimes/macos-<arch>`. CI runs the packaged app's `--diagnostics` and requires `[ok]   transcription`, under Rosetta too. The model (`ggml-small-q5_1.bin`, about 3x faster than real time on 8 threads; `large-v3-turbo` was slower than real time) is found by `TranscriptionModelLocator`: setting, the shipped `models` (`app\models` on Windows), then the data folder.
- `SubtitleSegmenter` cuts the words into cues with a `SubtitleProfile` (`Tutorial`: one line of 42 characters at most, aiming at 32, 1 to 5 s, a silence of 0.5 s ends a cue, lingering 0.5 s; values from Netflix's French guide and Arcom's charter, all in the profile). A silence splits the speech into stretches; each stretch is cut by dynamic programming over the cost of each cue: cut at a sentence end, then a comma, then before an opener (conjunction, relative, preposition), never after an article, preposition or subject pronoun; a cue under the minimum, a second line or a line past the target costs more. It also drops what Whisper writes over silence ("[Musique]", Amara.org credits). `SubtitleProfile.Check` gives the reading speed warnings the panel shows (15 then 17 characters per second).
- `SubtitleDocument` holds the editing rules: cues sorted and never overlapping, each at least 0.1 s; an edit that would break that is refused with a French message. `Timeline` is the cues and the gaps between them, covering the whole video, and `IndexAt` gives the entry at a position: the panel always shows a cue or a gap. Undo is `UndoHistory` swapping whole lists.
- `SubtitleEditor` (core) is the panel without its views: the job it follows or the `.srt` it loaded, `Status` (text, progress, "Réessayer"), `Timeline` and `ActiveIndex`, the checks of an edit (`CommitStart`, `CommitEnd`, `CommitText`), the save 0.5 s after the last change (on its `TimeProvider`), and `CueShown`. `CueReadability`, `SubtitlePlacement` and `SubtitleToggle` are the speed shown beside each cue, the band's place on the picture and the bar's toggle, for both apps.
- `TranscriptPanel` (Windows, left of the video in `VideoPreviewWindow`) and `TranscriptPanelView` (macOS, in an `NSSplitView` beside the `AVPlayerView`) build their rows in code. Cue rows are updated in place and only gap rows come and go, so the field being edited keeps the focus. The player's position is pushed on every tick (100 ms) and seek; the active row is centred, except when the seek came from a click in the panel. The panel raises `CueShown` with the text at the playing position; the player shows it over the picture (`SubtitleBand` on Windows, a label in the `AVPlayerView`'s overlay on macOS; translucent black, placed and sized on the picture rather than the letterbox). Entering a field pauses the video; the window's playback keys are ignored while a field has the keyboard. Every change is saved 0.5 s later; Supprimer/Refaire cancel the transcription and send the `.srt` to the Recycle Bin with the video.

### Settings and file names

- `ISettingsStore.Save` throws; `TrySave` swallows IO errors and is used for incidental state (last region, counter, editor preferences) so a locked file never interrupts a capture.
- `JsonSettingsStore` writes the keys of v0.1.0 plus `SchemaVersion`, through metadata generated at compile time (`SettingsJsonContext`) rather than reflection, which trimming would break; a key missing from the file keeps the platform default (`SettingsDefaults`), and a corrupt file gives the defaults.
- The settings window's checks and saving are `SettingsEditor.TryApply` (core); the window fills a `SettingsForm` and shows the message it returns.
- File names come from `FileNamePattern.Expand`, side-effect free and shared with the settings preview; videos use the same pattern. Invalid characters are those of Windows, on every platform, so a pattern names files alike everywhere. The `%NUM%` counter only advances after a successful save (`FileCounter`).
