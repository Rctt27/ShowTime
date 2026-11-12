using ShowTime.Core.Updates;

namespace ShowTime.Core.Ports;

/// <summary>Where the latest release is announced.</summary>
public interface IReleaseFeed
{
    /// <summary>The raw JSON of the latest release. Throws <see cref="HttpRequestException"/> on a failure.</summary>
    Task<string> GetLatestAsync(CancellationToken cancellationToken);
}

/// <summary>Downloads a release asset to a file; no partial file is left behind on failure or cancellation.</summary>
public interface IUpdateDownloader
{
    Task DownloadAsync(Uri url, string destinationFile, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>Stages a downloaded release and replaces the running app with it.</summary>
public interface IUpdateInstaller
{
    UpdatePlatform Platform { get; }

    UpdateLayout Layout { get; }

    /// <summary>True for a packaged install in a writable folder.</summary>
    bool CanReplaceApp();

    /// <summary>Unpacks the zip and returns the staged app. Throws <see cref="UpdateException"/>.</summary>
    Task<string> StageAsync(string zipFile, string stagingFolder, CancellationToken cancellationToken);

    /// <summary>Starts the replacement, detached; the app quits right after.</summary>
    void StartReplacement(ReplacementPlan plan);
}

/// <summary>What an update needs from the running app.</summary>
public interface IUpdateHost
{
    /// <summary>Closes editors and players, asking about unsaved work; false when the user cancelled.</summary>
    bool TryCloseWindows();

    void Quit();

    void OpenInBrowser(Uri url);
}

/// <summary>An update step that failed, with a message fit for the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
