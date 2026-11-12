using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;

namespace ShowTime.Core.Updates;

/// <summary>What the replacement script passed on the command line.</summary>
/// <param name="ConfirmFile">Set when this is the new version, which the script waits to hear from.</param>
/// <param name="FailedVersion">Set when this is the old version, started again because that one did not start.</param>
public sealed record UpdateArguments(string? ConfirmFile, SemanticVersion? FailedVersion)
{
    /// <summary>Reads <c>--update-confirm &lt;path&gt;</c> and <c>--update-failed &lt;X.Y.Z&gt;</c>, ignoring every other argument.</summary>
    public static UpdateArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? confirmFile = null;
        SemanticVersion? failedVersion = null;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == ReplacementPlan.ConfirmArgument)
            {
                confirmFile = args[++i];
            }
            else if (args[i] == ReplacementPlan.FailedArgument)
            {
                failedVersion = SemanticVersion.TryParse(args[++i], out var version) ? version : null;
            }
        }

        return new UpdateArguments(confirmFile, failedVersion);
    }
}

/// <summary>The end of an update, at the start of the version the script launched.</summary>
public static class UpdateStartup
{
    /// <summary>
    /// The new version confirms that it started; otherwise what an interrupted or failed update left is deleted, and the
    /// old version, started again, says that the new one did not start. Nothing here stops ShowTime from starting.
    /// </summary>
    public static void Run(UpdateArguments arguments, UpdateLayout layout, IFileSystem fileSystem, IUserDialogs dialogs,
        SemanticVersion current, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(log);

        if (arguments.ConfirmFile is { } confirmFile)
        {
            // The script is still running and deletes the staging folder and the backup itself.
            Confirm(confirmFile, fileSystem, current, log);
            return;
        }

        if (KeptBackup(layout, fileSystem, log) is { } backup)
        {
            // The script could not put the old version back: the backup may be its only copy, so nothing is deleted
            // until the user has moved or deleted it.
            log.LogWarning("An update could not be undone entirely; the old version is kept in {Backup}", backup);
            dialogs.ShowWarning($"La mise à jour n'a pas pu être annulée entièrement : l'ancienne version est conservée dans {backup}.");
            return;
        }

        DeleteLeftovers(layout, fileSystem, log);
        if (arguments.FailedVersion is { } failed)
        {
            log.LogWarning("Version {Failed} did not start; still running version {Current}", failed, current);
            dialogs.ShowWarning($"La version {failed} n'a pas pu démarrer ; ShowTime est resté en version {current}.");
        }
    }

    /// <summary>
    /// The backup an incomplete restore left, while it is still there; null otherwise. Once the user has moved or deleted
    /// it, as the warning asks, the marker protects nothing: it is deleted, so that updates and cleaning resume.
    /// </summary>
    internal static string? KeptBackup(UpdateLayout layout, IFileSystem fileSystem, ILogger log)
    {
        if (!fileSystem.File.Exists(layout.RestoreFailedMarker))
            return null;
        var backup = ReadBackupPath(layout, fileSystem);
        if (fileSystem.Directory.Exists(backup) || fileSystem.File.Exists(backup))
            return backup;

        try
        {
            fileSystem.File.Delete(layout.RestoreFailedMarker);
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("The backup {Backup} of an update that could not be undone is gone; marker removed", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not remove the marker {Marker}", layout.RestoreFailedMarker);
        }

        return null;
    }

    /// <summary>The backup's path, as the script wrote it in the marker; the layout's when it cannot be read.</summary>
    private static string ReadBackupPath(UpdateLayout layout, IFileSystem fileSystem)
    {
        try
        {
            var written = fileSystem.File.ReadAllText(layout.RestoreFailedMarker).Trim();
            return written.Length > 0 ? written : layout.BackupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return layout.BackupPath;
        }
    }

    private static void Confirm(string confirmFile, IFileSystem fileSystem, SemanticVersion current, ILogger log)
    {
        try
        {
            fileSystem.File.WriteAllText(confirmFile, current.ToString());
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Version {Current} started after an update", current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The script then puts the old version back.
            log.LogError(ex, "Could not confirm the start of version {Current} to the update script", current);
        }
    }

    private static void DeleteLeftovers(UpdateLayout layout, IFileSystem fileSystem, ILogger log)
    {
        IEnumerable<string> leftovers;
        try
        {
            leftovers = layout.Leftovers(fileSystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not look for what an update left in {Folder}", layout.AppPath);
            return;
        }

        foreach (var leftover in leftovers)
        {
            try
            {
                fileSystem.Directory.Delete(leftover, recursive: true);
                if (log.IsEnabled(LogLevel.Information))
                    log.LogInformation("Deleted {Leftover}, left by an update", leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use: the next start tries again.
                log.LogWarning(ex, "Could not delete {Leftover}, left by an update", leftover);
            }
        }
    }
}
