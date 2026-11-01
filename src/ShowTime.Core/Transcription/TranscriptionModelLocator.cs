using System.IO.Abstractions;

namespace ShowTime.Core.Transcription;

/// <summary>
/// Finds the Whisper model (a ggml .bin file) on this computer. The release ships one in a models folder (app\models
/// in the Windows zip); ShowTime never downloads one: that would be a network call.
/// </summary>
/// <param name="directories">Searched in order, after the configured path.</param>
public sealed class TranscriptionModelLocator(IFileSystem fileSystem, IReadOnlyList<string> directories)
{
    /// <returns>The model, or null when there is none.</returns>
    public string? Find(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return fileSystem.File.Exists(configuredPath) ? configuredPath : null;

        foreach (var directory in directories)
        {
            if (!fileSystem.Directory.Exists(directory))
                continue;
            var model = fileSystem.Directory.GetFiles(directory, "*.bin").Order(StringComparer.Ordinal).FirstOrDefault();
            if (model is not null)
                return model;
        }
        return null;
    }
}
