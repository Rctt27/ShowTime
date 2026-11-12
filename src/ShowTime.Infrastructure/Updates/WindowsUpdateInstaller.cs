using System.IO.Abstractions;
using System.IO.Compression;
using ShowTime.Core.Ports;
using ShowTime.Core.Updates;

namespace ShowTime.Infrastructure.Updates;

/// <summary>
/// Installs a new version over the folder of the Windows zip: <c>ShowTime.exe</c>, <c>LISEZ-MOI.txt</c> and <c>app\</c>.
/// </summary>
/// <param name="appFolder">The folder holding ShowTime.exe.</param>
public sealed class WindowsUpdateInstaller(IAppPaths paths, IFileSystem fileSystem, string appFolder) : IUpdateInstaller
{
    public UpdatePlatform Platform => UpdatePlatform.WindowsX64;

    public UpdateLayout Layout { get; } = new(UpdatePlatform.WindowsX64, appFolder);

    /// <summary>
    /// An install from the zip (its <c>app\ffmpeg.exe</c>; a build from the repository has none) in a folder ShowTime
    /// can write to.
    /// </summary>
    public bool CanReplaceApp()
    {
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(appFolder, "app", "ffmpeg.exe")))
            return false;
        var probe = fileSystem.Path.Combine(appFolder, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            fileSystem.File.WriteAllText(probe, "");
            fileSystem.File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Extracts the zip into <c>extracted</c> and returns the folder holding ShowTime.exe.</summary>
    public Task<string> StageAsync(string zipFile, string stagingFolder, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var extracted = fileSystem.Path.Combine(stagingFolder, "extracted");
        if (fileSystem.Directory.Exists(extracted))
            fileSystem.Directory.Delete(extracted, recursive: true);
        try
        {
            ZipFile.ExtractToDirectory(zipFile, extracted);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException("l'archive est illisible", ex);
        }

        // release.yml zips the ShowTime folder itself: its content is what gets installed.
        var root = extracted;
        if (fileSystem.Directory.GetFiles(root).Length == 0 && fileSystem.Directory.GetDirectories(root) is [var single])
            root = single;
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(root, "ShowTime.exe")))
            throw new UpdateException("l'archive ne contient pas ShowTime.exe");
        // The script skips what is missing: without its app folder, the new version could not record nor transcribe.
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(root, "app", "ffmpeg.exe")))
            throw new UpdateException(@"l'archive ne contient pas app\ffmpeg.exe");
        return root;
    }, cancellationToken);

    public void StartReplacement(ReplacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var script = ScriptLauncher.Write(Platform, paths.WorkFolder, ReplacementScript.ForWindows(plan));
        using var process = ScriptLauncher.StartDetached(Platform, script);
    }
}
