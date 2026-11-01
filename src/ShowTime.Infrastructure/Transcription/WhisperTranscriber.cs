using ShowTime.Core.Ports;
using ShowTime.Core.Transcription;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace ShowTime.Infrastructure.Transcription;

/// <summary>
/// Whisper (whisper.cpp through Whisper.net), on this computer's processor. Never its model downloader: ShowTime makes
/// no network call.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    /// <summary>
    /// Looks for Whisper's native libraries in <paramref name="folder"/>\runtimes\&lt;platform&gt; before the runtimes
    /// folder next to the assemblies (a development build). Call it before the first transcription.
    /// </summary>
    public static void LoadNativeLibrariesFrom(string folder) =>
        // Whisper.net searches the runtimes folder of this path's directory first; the file itself need not exist.
        RuntimeOptions.LibraryPath = Path.Combine(folder, "whisper");

    /// <summary>
    /// Word by word: a segment of one character at most, cut between words, is one word with its punctuation. The
    /// subtitles are cut from them by the core (<see cref="SubtitleSegmenter"/>), which knows about pauses and grammar.
    /// </summary>
    public Task<IReadOnlyList<TranscribedWord>> TranscribeAsync(
        string audioPath, string modelPath, string language, Action<double> onProgress, CancellationToken cancellationToken)
    {
        // Loading the model and transcribing take seconds to minutes of processor: never on the caller's thread.
        return Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            var builder = factory.CreateBuilder()
                // Half the cores: the computer stays usable while it runs.
                .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(1)
                .WithProgressHandler(percent => onProgress(percent / 100.0));
            builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
            using var processor = builder.Build();
            await using var audio = File.OpenRead(audioPath);

            var words = new List<TranscribedWord>();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
                words.Add(new TranscribedWord(segment.Start, segment.End, segment.Text));
            return (IReadOnlyList<TranscribedWord>)words;
        }, cancellationToken);
    }

    /// <summary>
    /// Whether the native library loads the model, for the diagnostics; the message says why when it does not.
    /// </summary>
    public static string? CheckRuntime(string modelPath)
    {
        try
        {
            // The factory only loads the model for its first processor.
            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = factory.CreateBuilder().Build();
            return null;
        }
#pragma warning disable CA1031 // Reported by the diagnostics, whatever the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }
}
