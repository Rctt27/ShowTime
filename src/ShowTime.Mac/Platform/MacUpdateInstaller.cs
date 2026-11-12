using System.Diagnostics;
using Foundation;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;
using ShowTime.Core.Updates;
using ShowTime.Infrastructure.Updates;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac.Platform;

/// <summary>
/// Installs a new version over ShowTime.app, where it is. The new bundle must be signed like this one: macOS keeps the
/// Screen Recording and Microphone permissions for the same signature only.
/// </summary>
internal sealed class MacUpdateInstaller(IAppPaths paths, ILogger<MacUpdateInstaller> log) : IUpdateInstaller
{
    private const string SignatureMismatch = "la signature de la nouvelle version ne correspond pas";

    public UpdatePlatform Platform => UpdatePlatform.MacOS;

    public UpdateLayout Layout { get; } = new(UpdatePlatform.MacOS, NSBundle.MainBundle.BundlePath);

    /// <summary>
    /// A packaged app (its ffmpeg in Contents/MacOS; a development build has none) whose bundle and folder ShowTime can
    /// write to: not on a disk image, nor in an /Applications owned by root.
    /// </summary>
    public bool CanReplaceApp() =>
        File.Exists(Path.Combine(Layout.AppPath, "Contents", "MacOS", "ffmpeg"))
        && CanWrite(Path.GetDirectoryName(Layout.AppPath)!) && CanWrite(Layout.AppPath);

    public async Task<string> StageAsync(string zipFile, string stagingFolder, CancellationToken cancellationToken)
    {
        var extracted = Path.Combine(stagingFolder, "extracted");
        if (Directory.Exists(extracted))
            Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        // ditto keeps the bundle's symbolic links and its signature, which a plain unzip can break.
        if ((await RunAsync("/usr/bin/ditto", ["-x", "-k", zipFile, extracted], cancellationToken).ConfigureAwait(false)).Exit != 0)
            throw new UpdateException("l'archive est illisible");
        var app = Path.Combine(extracted, "ShowTime.app");
        if (!Directory.Exists(app))
            throw new UpdateException("l'archive ne contient pas ShowTime.app");

        // Downloaded by ShowTime rather than a browser, it should carry no quarantine; this makes sure of it, since
        // Gatekeeper would refuse an app that is not notarized.
        await RunAsync("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app], cancellationToken).ConfigureAwait(false);
        await CheckSignatureAsync(app, cancellationToken).ConfigureAwait(false);
        return app;
    }

    public void StartReplacement(ReplacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var script = ScriptLauncher.Write(Platform, paths.WorkFolder, ReplacementScript.ForMacOS(plan));
        using var process = ScriptLauncher.StartDetached(Platform, script);
    }

    /// <summary>
    /// The new bundle must be valid and meet this one's designated requirement (same identifier, same certificate).
    /// A build signed ad hoc has no certificate to compare: it only accepts another ad hoc build.
    /// </summary>
    private async Task CheckSignatureAsync(string app, CancellationToken cancellationToken)
    {
        var running = await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", Layout.AppPath], cancellationToken).ConfigureAwait(false);
        string[] verify;
        if (CodeSignature.IsAdHoc(running.Output))
        {
            var staged = await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", app], cancellationToken).ConfigureAwait(false);
            if (!CodeSignature.IsAdHoc(staged.Output))
                throw new UpdateException(SignatureMismatch);
            verify = ["--verify", "--deep", "--strict", app];
        }
        else
        {
            var requirement = CodeSignature.DesignatedRequirement(
                (await RunAsync("/usr/bin/codesign", ["-d", "-r-", Layout.AppPath], cancellationToken).ConfigureAwait(false)).Output)
                ?? throw new UpdateException(SignatureMismatch);
            // "-R=" followed by the requirement's text: codesign reads it as text rather than as a file.
            verify = ["--verify", "--deep", "--strict", "-R=" + requirement, app];
        }

        var check = await RunAsync("/usr/bin/codesign", verify, cancellationToken).ConfigureAwait(false);
        if (check.Exit != 0)
        {
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning("The new version's signature was refused: {Output}", check.Output);
            throw new UpdateException(SignatureMismatch);
        }
    }

    /// <summary>Runs a tool and returns its exit code with what it printed on both outputs (codesign -d writes on stderr).</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string file, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new UpdateException(file + " n'a pas pu démarrer");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, ".ShowTime-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
