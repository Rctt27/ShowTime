using System.IO;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Windows.Tests.Recording;

/// <summary>
/// The transcriptions of a temporary folder, on the real file system, with a transcriber the test completes by hand and
/// an extractor that writes an empty WAV.
/// </summary>
internal sealed class TestTranscriptions
{
    public TestTranscriptions(string folder, bool withModel = true)
    {
        var fileSystem = new FileSystem();
        var models = Path.Combine(folder, "models");
        Directory.CreateDirectory(models);
        if (withModel)
            File.WriteAllText(Path.Combine(models, "ggml-small-q5_1.bin"), "");
        var paths = Substitute.For<IAppPaths>();
        paths.WorkFolder.Returns(Path.Combine(folder, "work"));
        var settings = new AppSettings(SettingsDefaults.ForWindows(folder));
        var locator = new TranscriptionModelLocator(fileSystem, [models]);
        Subtitles = new SubtitleFile(fileSystem);
        Queue = new Transcriptions(Transcriber, Extractor, new MicrophoneTracks(settings, locator, paths, fileSystem), locator,
            settings, Subtitles, fileSystem, Substitute.For<IRecordingStatus>(), new ImmediateUiThread(),
            NullLogger<Transcriptions>.Instance);
    }

    public ManualTranscriber Transcriber { get; } = new();
    public EmptyWavExtractor Extractor { get; } = new();
    public SubtitleFile Subtitles { get; }
    public Transcriptions Queue { get; }

    private sealed class ImmediateUiThread : IUiThread
    {
        public void Post(Action action) => action();
    }
}

internal sealed class ManualTranscriber : ITranscriber
{
    private TaskCompletionSource<IReadOnlyList<TranscribedWord>>? _result;

    public string? AudioPath { get; private set; }
    public Action<double>? Progress { get; private set; }

    public void Complete(IReadOnlyList<TranscribedWord> words) => _result!.SetResult(words);

    public Task<IReadOnlyList<TranscribedWord>> TranscribeAsync(
        string audioPath, string modelPath, string language, Action<double> onProgress, CancellationToken cancellationToken)
    {
        AudioPath = audioPath;
        Progress = onProgress;
        _result = new TaskCompletionSource<IReadOnlyList<TranscribedWord>>();
        cancellationToken.Register(() => _result.TrySetCanceled(cancellationToken));
        return _result.Task;
    }
}

internal sealed class EmptyWavExtractor : IAudioExtractor
{
    public string? VideoPath { get; private set; }

    public Task ExtractAsync(string videoPath, string audioPath, CancellationToken cancellationToken)
    {
        VideoPath = videoPath;
        File.WriteAllText(audioPath, "");
        return Task.CompletedTask;
    }
}
