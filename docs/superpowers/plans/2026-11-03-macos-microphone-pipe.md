# Mac microphone through ShowTime's pipe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On a Mac, the microphone keeps every sample: ShowTime captures it itself and pipes it to ffmpeg, as it already does for the Mac's sound.

**Architecture:** ffmpeg 9.0's `avfoundation` input holds one audio buffer (512 samples, 10.7 ms) and overwrites it when it is not read in time; it is read by polling every 10 ms, and ffmpeg's scheduler pauses an input more than 100 ms ahead of the video. A recording made on 2026-11-03 kept 9.8 s of a 23.25 s talk, stamped over the whole video: played back to back, the voice ran 2.4 times too fast, then stopped. The fix: a new port `IMicrophoneSource` returns an `IAudioCapture` (the renamed `ISystemAudioCapture`), the factory starts it before ffmpeg, and `AvFoundation` reads it as the first trailing pipe input. The Mac adapter uses `AVCaptureSession` + `AVCaptureAudioDataOutput` and the existing `MacAudioPipe`, whose `AudioTimeline` places each packet at its time.

**Tech Stack:** .NET 10, xunit v3 + Shouldly, .NET for macOS (AVFoundation, CoreMedia).

**Spec:** this plan; root cause in the Goal and Architecture above.

## Global Constraints

- No network, no telemetry: the capture stays in-process.
- Windows command lines must not change (`tests/fixtures/legacy`, `DirectShowGdigrabTests`): Windows passes no microphone source.
- The microphone stays the first audio stream: `amix` order and the transcription track (`MicrophoneTracks`) read it.
- Mic pipe format: `f32le`, 48000 Hz, 1 channel.
- The macOS app has no automated tests and builds only on a Mac: its code is checked by CI (`ShowTime.Mac.slnx` job) and by hand with `docs/test-checklist.md`.
- No test runs locally without the user's go: verify through CI on the pull request.
- UI and log strings French where user-facing; code and comments English.

## Review Focus

- The chosen microphone is not found by name (unplugged, renamed): record the default microphone and log a warning, rather than fail.
- Microphone permission refused: the pipe goes on with silence (padding) and a warning is logged; the video is still recorded.
- Speech in the first instants after 0: the session starts before ffmpeg, so packets straddling the pipe's opening are trimmed, not lost (`AudioTimeline.Place`).
- Sample times on another clock than the host clock: converted from the session's `SynchronizationClock`; otherwise `AudioTimeline.MaxJump` re-anchors and the count is logged.
- Microphone and Mac's sound together: two pipes after the screen, the microphone first, mixed by `amix`.

---

### Task 1: Core — microphone as the first trailing pipe input

**Files:**
- Modify: `src/ShowTime.Core/Ports/IRecordingPorts.cs` (rename `ISystemAudioCapture` → `IAudioCapture`; add `IMicrophoneSource`)
- Modify: `src/ShowTime.Core/Recording/IFfmpegPlatform.cs`, `AvFoundation.cs`, `DirectShowGdigrab.cs`, `CaptureInputs.cs`, `RecordingCommand.cs`
- Modify (rename only): `src/ShowTime.Platform.Windows/WasapiSystemAudio.cs`, `src/ShowTime.Mac/Platform/MacSystemAudio.cs`, `tests/ShowTime.Platform.Tests/Windows/WasapiSystemAudioTests.cs`, `tests/ShowTime.Infrastructure.IntegrationTests/Recording/FfmpegRecorderTests.cs`
- Test: `tests/ShowTime.Core.Tests/Recording/AvFoundationTests.cs`, `RecordingCommandTests.cs`

**Interfaces:**
- Produces:
  - `public interface IAudioCapture : IDisposable { PipedAudio Input { get; } }` (was `ISystemAudioCapture`); `ISystemAudioSource.Start()` returns it.
  - `public interface IMicrophoneSource { IAudioCapture Start(string microphone); }` — opens the named microphone (as `IFfmpegPlatform.ParseDeviceList` names it) and a pipe for ffmpeg.
  - `IFfmpegPlatform.BuildInputs(PixelRect area, VideoOptions options, PipedAudio? systemAudio, PipedAudio? microphone = null)`.
  - `CaptureInputs(..., bool MicrophoneTrails = false)`: the microphone is the first trailing input rather than the first leading one.

- [ ] **Step 1: Write the failing tests** in `AvFoundationTests` (replace `The_microphone_is_opened_by_name`; deliberate change, comment it):
  - `The_microphone_is_read_from_its_pipe_after_the_screen`: `BuildInputs(area, new VideoOptions(24, "MacBook Pro Microphone", false), null, MicPipe)` with `MicPipe = new("/tmp/mic.pcm", new RawAudioFormat("f32le", 48000, 1))` → `AudioInputs` empty; `TrailingAudioInputs` = `[["-f","f32le","-ar","48000","-ac","1","-thread_queue_size","1024","-probesize","32","-analyzeduration","0","-i","/tmp/mic.pcm"]]`; `MicrophoneTrails` true; `VideoInput` contains `-probesize`; no argument is `:MacBook Pro Microphone`.
  - `The_microphone_comes_before_the_computer_sound`: mic + system → trailing `[mic pipe args, system pipe args]` in that order.
  - `The_microphone_needs_its_pipe`: `VideoOptions(30, "MacBook Pro Microphone", false)` with `microphone: null` → `ArgumentException`.
  - Update `The_computer_sound_is_read_from_the_pipe_after_the_screen` to pass `MicPipe` and expect the two trailing inputs.
  
  In `RecordingCommandTests`:
  - `A_trailing_microphone_is_the_track_and_the_first_in_the_mix`: `new CaptureInputs([], ["-i","screen"], TrailingAudioInputs: [["-i","mic"],["-i","late"]], MicrophoneTrails: true)` with `"mic.wav"` → `amix` of `[1:a][2:a]` and the track ends with `"-map","1:a","-ac","1","-ar","16000","-c:a","pcm_s16le","mic.wav"`.
  - Keep `A_microphone_track_needs_a_microphone` unchanged (trailing input without `MicrophoneTrails` still throws).

- [ ] **Step 2: Build** `dotnet build ShowTime.slnx -c Release` — expected: compile errors in the new tests (no `microphone` parameter, no `MicrophoneTrails`).

- [ ] **Step 3: Implement.** `AvFoundation.BuildInputs`: a non-empty `options.Microphone` requires `microphone` (else `ArgumentException`, as for the system sound); the trailing inputs are the mic pipe then the system pipe, each built by one `PipeInput(PipedAudio)` helper; `NoProbe` on the screen whenever a pipe trails; `MicrophoneTrails: microphone is not null`. `DirectShowGdigrab` ignores the new parameter. `RecordingCommand.Build`: the track maps the first trailing index when `MicrophoneTrails`, input 0 when there is a leading audio input, and throws otherwise. Doc of `CaptureInputs.AudioInputs` and `IFfmpegPlatform.BuildInputs` updated. Rename `ISystemAudioCapture` everywhere.

- [ ] **Step 4: Build and format** `dotnet build ShowTime.slnx -c Release` and `dotnet format ShowTime.slnx --verify-no-changes` — expected: success.

- [ ] **Step 5: Commit** "Read the Mac microphone from a pipe after the screen".

### Task 2: Infrastructure — the factory starts the microphone capture

**Files:**
- Modify: `src/ShowTime.Infrastructure/Recording/FfmpegVideoRecorderFactory.cs`, `FfmpegVideoRecorder.cs` (parameter `systemAudio` → `audio`, doc only)
- Test: `tests/ShowTime.Infrastructure.IntegrationTests/Recording/FfmpegRecorderTests.cs`

**Interfaces:**
- Consumes: `IMicrophoneSource`, `IAudioCapture`, `BuildInputs(..., microphone)` from Task 1.
- Produces: `FfmpegVideoRecorderFactory(IFfmpegPlatform platform, ISystemAudioSource? systemAudio = null, IMicrophoneSource? microphone = null)`.

- [ ] **Step 1: Write the failing tests** (`FakeMicrophone : IMicrophoneSource` records the name and its `FakeCapture`; `TestSourcePlatform` records `Microphone`):
  - `The_factory_pipes_the_microphone_of_its_source` (needs ffmpeg): `VideoOptions(30, "Mic A", false)` → `source.Name == "Mic A"`, `platform.Microphone` is the capture's `Input`; after `StopAsync()` the capture is disposed.
  - `Without_a_microphone_the_factory_opens_none`: `VideoOptions(30, null, false)` with a non-ffmpeg path → `Win32Exception`, no capture started.
  - `A_failed_launch_frees_the_microphone`: `VideoOptions(30, "Mic A", true)`, non-ffmpeg path → `Win32Exception`, both mic and system captures disposed.

- [ ] **Step 2: Build** — expected: compile error (no third constructor parameter).

- [ ] **Step 3: Implement.** The factory starts the mic capture when `options.Microphone` is non-empty and a source is given, before ffmpeg; passes both pipes to `BuildInputs`; hands the recorder one `IDisposable` freeing both (a private `AudioCaptures` class); on a throw, frees both.

- [ ] **Step 4: Build and format** — expected: success.

- [ ] **Step 5: Commit** "Start the microphone capture with the recorder".

### Task 3: Mac — capture the microphone with AVFoundation

**Files:**
- Create: `src/ShowTime.Mac/Platform/MacMicrophone.cs` (`MacMicrophone : IMicrophoneSource`, `MacMicrophoneCapture : IAudioCapture`)
- Modify: `src/ShowTime.Mac/Composition.cs` (register `IMicrophoneSource`), `src/ShowTime.Mac/StatusMenu.cs` (comment: ShowTime records the microphone itself)

**Interfaces:**
- Consumes: `MacAudioPipe(string directory, RawAudioFormat format, Func<TimeSpan> clock)`, `IMicrophoneSource`.

- [ ] **Step 1: Implement `MacMicrophoneCapture(string microphone, ILogger log)`.**
  - Format `new RawAudioFormat("f32le", 48000, 1)`; pipe on the host clock (`CMClock.HostTimeClock`), like `MacSystemAudioCapture`.
  - Device: the `AVCaptureDeviceDiscoverySession` of `Microphone` and `External` audio devices whose `LocalizedName` is `microphone`, else `AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio)` with a warning.
  - `AVCaptureSession` with that input and an `AVCaptureAudioDataOutput` whose `AudioSettings` ask linear PCM float 32 bits, 48000 Hz, 1 channel, interleaved, little endian; delegate on its own `DispatchQueue`.
  - The session starts on a pool thread at construction (before ffmpeg opens the pipe: no start-up delay cuts the first words); the pipe ignores packets until then.
  - Each buffer: check the format (32 bits, 48000, 1 channel) and the byte count, copy it pinned, convert its time to the host clock with the session's `SynchronizationClock` when there is one, `pipe.Write(at, pcm)`; dispose the sample buffer. A throw in the callback is caught and logged once (warning), as in `MacSystemAudio`; any setup failure is logged and the pipe goes on with silence.
  - Log the first packet's offset from the host clock (information) and, on disposal, `pipe.Reanchors` when non-zero (warning).
  - `Dispose`: wait for the start (1 s at most), `StopRunning`, remove the delegate, dispose the session, then the pipe.

- [ ] **Step 2: Register** `services.AddSingleton<IMicrophoneSource, MacMicrophone>();` next to `ISystemAudioSource`; the factory gets it by injection.

- [ ] **Step 3: Format by hand** (`System` usings first, then alphabetical) and push; CI's macOS job builds `ShowTime.Mac.slnx` with trim warnings as errors — expected: green.

- [ ] **Step 4: Commit** "Capture the Mac microphone with AVFoundation".

### Task 4: Documentation

**Files:**
- Modify: `CLAUDE.md` (Video: the Mac microphone; Transcription: "always `AudioInputs[0]`" → the first audio input), `docs/test-checklist.md` (macOS: talk 20 s non-stop, the video's voice is at normal speed and lasts the whole video, with and without the Mac's sound)

- [ ] **Step 1: Write both changes.**
- [ ] **Step 2: Commit** "Document the Mac microphone's pipe".

### Task 5: Pull request

- [ ] **Step 1:** Push the branch, open the PR, wait for CI (Windows and macOS jobs) — expected: green; `MacAudioPipeTests` may need `gh run rerun <id> --failed`.
- [ ] **Step 2:** Ask the user to check the CI artifact `ShowTime-macos` with the checklist item before merging.
