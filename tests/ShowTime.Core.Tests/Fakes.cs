using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests;

internal sealed class FakeImage(int width = 10, int height = 10) : IImage
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

/// <summary>Runs posted actions at once, as if every caller were on the UI thread.</summary>
internal sealed class ImmediateUiThread : IUiThread
{
    public void Post(Action action) => action();
}

/// <summary>A UI thread that is stuck until the test runs what was posted to it.</summary>
internal sealed class ManualUiThread : IUiThread
{
    private readonly Queue<Action> _posted = new();

    public int Pending => _posted.Count;

    public void Post(Action action) => _posted.Enqueue(action);

    public void RunAll()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }
}

/// <summary>Notes the hangs reported and the recoveries; <see cref="OnHung"/> stands for the user's answer.</summary>
internal sealed class FakeHangResponder : IHangResponder
{
    public List<(TimeSpan Silence, bool First)> Hangs { get; } = [];
    public List<TimeSpan> Recoveries { get; } = [];
    public Action? OnHung { get; set; }

    public void Hung(TimeSpan silence, bool first)
    {
        Hangs.Add((silence, first));
        OnHung?.Invoke();
    }

    public void Recovered(TimeSpan silence) => Recoveries.Add(silence);
}

/// <summary>Keeps every line logged, for the test to read.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}

/// <summary>A settings store that keeps nothing.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    public int Saves { get; private set; }
    public AppSettings Load() => new(SettingsDefaults.ForWindows("C:\\Out"));
    public void Save(AppSettings settings) => Saves++;
    public bool TrySave(AppSettings settings)
    {
        Saves++;
        return true;
    }
}

internal sealed class FakeHotkeys : IGlobalHotkeys
{
    private readonly Dictionary<Hotkey, Action> _registered = [];

    public HashSet<Hotkey> Refused { get; } = [];
    public IReadOnlyCollection<Hotkey> Registered => _registered.Keys;

    public bool Register(Hotkey hotkey, Action action)
    {
        if (Refused.Contains(hotkey))
            return false;
        _registered[hotkey] = action;
        return true;
    }

    public void Press(string text)
    {
        Hotkey.TryParse(text, out var hotkey).ShouldBeTrue();
        _registered[hotkey]();
    }

    public void UnregisterAll() => _registered.Clear();
    public void Dispose() => UnregisterAll();
}

internal sealed class FakeRecordingView : IRecordingView
{
    public List<string> Calls { get; } = [];
    public IReadOnlyCollection<string>? Devices { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>Kept apart from <see cref="Calls"/>: the trigger shown during the recording.</summary>
    public string? HighlightShortcut { get; private set; }
    public int HighlightShown { get; private set; }

    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? CancelRequested;

    public void ClickStart() => StartRequested?.Invoke(this, EventArgs.Empty);
    public void ClickStop() => StopRequested?.Invoke(this, EventArgs.Empty);
    public void ClickCancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    public void ShowReady(string? startShortcut, string? stopShortcut) => Calls.Add($"ready {startShortcut} {stopShortcut}");
    public void SetAudioDevices(IReadOnlyCollection<string> devices) => Devices = devices;
    public void ShowCountdown(int seconds) => Calls.Add($"countdown {seconds}");
    public void ShowStarting() => Calls.Add("starting");
    public void ShowRecording() => Calls.Add("recording");
    public void ShowFinishing() => Calls.Add("finishing");

    public void ShowHighlightShortcut(string? shortcut)
    {
        HighlightShortcut = shortcut;
        HighlightShown++;
    }

    public void Dispose() => Closed = true;
}

internal sealed class FakeRecordingViewFactory : IRecordingViewFactory
{
    public FakeRecordingView? View { get; private set; }
    public PixelRect? Area { get; private set; }
    public bool? FullScreen { get; private set; }

    public IRecordingView Show(PixelRect area, bool fullScreen)
    {
        Area = area;
        FullScreen = fullScreen;
        return View = new FakeRecordingView();
    }
}

internal sealed class FakeRecorder(string outputPath) : IVideoRecorder
{
    public TaskCompletionSource<bool> CapturingSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> StopResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<IVideoRecorder>? OnUnexpectedExit { get; set; }

    public string OutputPath { get; } = outputPath;
    public string ErrorTail { get; set; } = "";
    public Task<bool> Capturing => CapturingSource.Task;
    public bool StopRequested { get; private set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }

    public Task<bool> StopAsync()
    {
        StopRequested = true;
        return StopResult.Task;
    }

    public void Kill() => Killed = true;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeRecorderFactory : IVideoRecorderFactory
{
    public FakeRecorder? Recorder { get; private set; }
    public VideoOptions? Options { get; private set; }
    public PixelRect? Area { get; private set; }
    public Exception? StartFailure { get; set; }

    /// <summary>The clock whose time is noted at each launch, in <see cref="StartedAt"/>.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Forgets the last recorder, so that a test can wait for the next one.</summary>
    public void Reset() => Recorder = null;

    public IVideoRecorder Start(string ffmpegPath, PixelRect area, string outputPath, VideoOptions options, Action<IVideoRecorder> onUnexpectedExit)
    {
        if (StartFailure is not null)
            throw StartFailure;
        Area = area;
        Options = options;
        StartedAt = Clock.GetUtcNow();
        return Recorder = new FakeRecorder(outputPath) { OnUnexpectedExit = onUnexpectedExit };
    }
}

internal static class FakeTime
{
    /// <summary>Advances the clock in small steps until the task completes, as the real one would.</summary>
    public static async Task RunUntil(this FakeTimeProvider clock, Task task, int maxSeconds = 60)
    {
        for (var elapsed = 0; !task.IsCompleted && elapsed < maxSeconds * 10; elapsed++)
        {
            await Task.Delay(1);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        await task;
    }
}

internal sealed class FakeRecordingStatus : IRecordingStatus
{
    public bool IsBusy => IsRecording;
    public bool IsRecording { get; private set; }
    public event EventHandler? StateChanged;

    public void Set(bool recording)
    {
        IsRecording = recording;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Writes the WAV ffmpeg would extract, or fails as told.</summary>
internal sealed class FakeAudioExtractor(System.IO.Abstractions.IFileSystem fileSystem) : IAudioExtractor
{
    public List<(string Video, string Audio)> Calls { get; } = [];
    public Exception? Failure { get; set; }

    public Task ExtractAsync(string videoPath, string audioPath, CancellationToken cancellationToken)
    {
        Calls.Add((videoPath, audioPath));
        if (Failure is not null)
            return Task.FromException(Failure);
        fileSystem.File.WriteAllText(audioPath, "RIFF");
        return Task.CompletedTask;
    }
}

/// <summary>Each transcription waits until the test completes, fails or cancels it.</summary>
internal sealed class FakeTranscriber : ITranscriber
{
    public sealed record Call(
        string AudioPath, string ModelPath, string Language, Action<double> OnProgress,
        TaskCompletionSource<IReadOnlyList<TranscribedWord>> Result, CancellationToken Token)
    {
        public void Complete(params TranscribedWord[] words) => Result.SetResult(words);
    }

    public List<Call> Calls { get; } = [];

    public Call Last => Calls[^1];

    public Task<IReadOnlyList<TranscribedWord>> TranscribeAsync(
        string audioPath, string modelPath, string language, Action<double> onProgress, CancellationToken cancellationToken)
    {
        // Continuations run inline: each step of the queue happens before the test's next line.
        var result = new TaskCompletionSource<IReadOnlyList<TranscribedWord>>();
        cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
        Calls.Add(new Call(audioPath, modelPath, language, onProgress, result, cancellationToken));
        return result.Task;
    }
}

internal sealed class FakeHighlightInput : IHighlightInput
{
    private Action? _pressed;

    public HighlightTrigger? Registered { get; private set; }
    public bool Refused { get; set; }
    public int Unregistrations { get; private set; }
    public bool IsHeld { get; set; }
    public PixelPoint Cursor { get; set; }

    public bool Register(HighlightTrigger trigger, Action pressed)
    {
        if (Refused)
            return false;
        Registered = trigger;
        _pressed = pressed;
        return true;
    }

    public void Unregister()
    {
        Unregistrations++;
        Registered = null;
        _pressed = null;
    }

    /// <summary>Presses the trigger, if it is registered.</summary>
    public void Press() => _pressed?.Invoke();
}

internal sealed class FakeHighlightOverlay : IHighlightOverlay
{
    public double Scale { get; set; } = 1;
    public List<(IReadOnlyList<StrokeDisc> Stroke, PixelRect Changed)> Draws { get; } = [];

    /// <summary>"Draw" and "SetOpacity", in the order they came.</summary>
    public List<string> Calls { get; } = [];
    public double Opacity { get; private set; }
    public int OpacityChanges { get; private set; }
    public bool Visible { get; private set; }
    public bool Disposed { get; private set; }

    public void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed)
    {
        Draws.Add(([.. stroke], changed));
        Calls.Add("Draw");
        Visible = true;
    }

    public void SetOpacity(double opacity)
    {
        Opacity = opacity;
        OpacityChanges++;
        Calls.Add("SetOpacity");
    }

    public void Hide() => Visible = false;
    public void Dispose() => Disposed = true;
}

internal static class UpdateFixtures
{
    /// <summary>GitHub's answer for the v0.6.3 release, trimmed to what ShowTime reads.</summary>
    public static string Release063 =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "release-v0.6.3.json"));
}

/// <summary>Answers with <see cref="Json"/>, fails with <see cref="Failure"/>, or waits on <see cref="Pending"/>.</summary>
internal sealed class FakeReleaseFeed : IReleaseFeed
{
    public int Calls { get; private set; }
    public string Json { get; set; } = UpdateFixtures.Release063;
    public Exception? Failure { get; set; }

    /// <summary>When set, each call waits until the test completes it.</summary>
    public TaskCompletionSource<string>? Pending { get; set; }

    public Task<string> GetLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (Pending is not null)
            return Pending.Task;
        return Failure is not null ? Task.FromException<string>(Failure) : Task.FromResult(Json);
    }
}

/// <summary>Writes <see cref="Content"/> to the destination after reporting <see cref="Progress"/>, or fails as told.</summary>
internal sealed class FakeUpdateDownloader(System.IO.Abstractions.IFileSystem fileSystem) : IUpdateDownloader
{
    public List<(Uri Url, string Destination)> Calls { get; } = [];
    public byte[] Content { get; set; } = [];
    public IReadOnlyList<double> Progress { get; set; } = [];
    public Exception? Failure { get; set; }

    /// <summary>When set, each download waits until the test completes it.</summary>
    public TaskCompletionSource? Pending { get; set; }

    public async Task DownloadAsync(Uri url, string destinationFile, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Calls.Add((url, destinationFile));
        foreach (var value in Progress)
            progress.Report(value);
        if (Pending is not null)
            await Pending.Task.WaitAsync(cancellationToken);
        if (Failure is not null)
            throw Failure;
        fileSystem.File.WriteAllBytes(destinationFile, Content);
    }
}

internal sealed class FakeUpdateInstaller : IUpdateInstaller
{
    public UpdatePlatform Platform { get; set; } = UpdatePlatform.WindowsX64;
    public UpdateLayout Layout { get; set; } = new(UpdatePlatform.WindowsX64, @"C:\ShowTime");
    public bool CanReplace { get; set; } = true;
    public Exception? StageFailure { get; set; }
    public List<(string Zip, string Staging)> Staged { get; } = [];
    public List<ReplacementPlan> Replacements { get; } = [];

    public bool CanReplaceApp() => CanReplace;

    public Task<string> StageAsync(string zipFile, string stagingFolder, CancellationToken cancellationToken)
    {
        Staged.Add((zipFile, stagingFolder));
        return StageFailure is not null
            ? Task.FromException<string>(StageFailure)
            : Task.FromResult(Path.Combine(stagingFolder, "ShowTime"));
    }

    public void StartReplacement(ReplacementPlan plan) => Replacements.Add(plan);
}

internal sealed class FakeUpdateHost : IUpdateHost
{
    public bool CloseWindows { get; set; } = true;
    public int CloseRequests { get; private set; }
    public int Quits { get; private set; }
    public List<Uri> Opened { get; } = [];

    public bool TryCloseWindows()
    {
        CloseRequests++;
        return CloseWindows;
    }

    public void Quit() => Quits++;
    public void OpenInBrowser(Uri url) => Opened.Add(url);
}

internal sealed class FakeHighlightOverlayFactory : IHighlightOverlayFactory
{
    public FakeHighlightOverlay? Overlay { get; private set; }
    public PixelRect? Area { get; private set; }

    /// <summary>The scale of the overlays it creates.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Throws as the system does when it cannot create the overlay's surface.</summary>
    public bool Fails { get; set; }

    public IHighlightOverlay Create(PixelRect area)
    {
        if (Fails)
            throw new InvalidOperationException("No surface.");
        Area = area;
        return Overlay = new FakeHighlightOverlay { Scale = Scale };
    }
}
