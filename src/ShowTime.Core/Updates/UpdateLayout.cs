using System.IO.Abstractions;

namespace ShowTime.Core.Updates;

/// <summary>
/// Where an update is staged and where the replaced version is kept until the new one has started, on the same volume
/// as the app so that every move is a rename. Paths use the platform's separator whatever the OS running the code.
/// </summary>
/// <param name="Platform">The installed app's platform.</param>
/// <param name="AppPath">Windows: the folder holding <c>ShowTime.exe</c>; macOS: the <c>.app</c> bundle.</param>
public sealed record UpdateLayout(UpdatePlatform Platform, string AppPath)
{
    private const string WindowsStagingPrefix = ".update-";
    private const string WindowsBackupName = ".old";
    private const string MacStagingPrefix = ".ShowTime-update-";
    private const string MacBackupName = ".ShowTime-old.app";
    private const string WindowsRestoreFailedName = ".update-failed";
    private const string MacRestoreFailedName = ".ShowTime-update-failed";

    /// <summary>The app's path, without a trailing separator (<c>AppContext.BaseDirectory</c> ends with one).</summary>
    public string AppPath { get; init; } = AppPath.TrimEnd('\\', '/');

    private char Separator => Platform == UpdatePlatform.WindowsX64 ? '\\' : '/';

    /// <summary>The folder holding what the update replaces and what it leaves: the install folder, or the bundle's parent.</summary>
    private string Folder
    {
        get
        {
            if (Platform == UpdatePlatform.WindowsX64)
                return AppPath;
            var slash = AppPath.LastIndexOf('/');
            return slash > 0 ? AppPath[..slash] : "/";
        }
    }

    private string StagingPrefix => Platform == UpdatePlatform.WindowsX64 ? WindowsStagingPrefix : MacStagingPrefix;

    private string BackupName => Platform == UpdatePlatform.WindowsX64 ? WindowsBackupName : MacBackupName;

    /// <summary>Where a version is downloaded and extracted.</summary>
    public string StagingFolder(SemanticVersion version) => Join(StagingPrefix + version);

    /// <summary>Windows: the folder the replaced items are moved into; macOS: the replaced bundle's new path.</summary>
    public string BackupPath => Join(BackupName);

    /// <summary>
    /// A file the replacement script writes when it could not put the old version back, holding the backup's path: the
    /// backup is then kept rather than deleted as a leftover.
    /// </summary>
    public string RestoreFailedMarker =>
        Join(Platform == UpdatePlatform.WindowsX64 ? WindowsRestoreFailedName : MacRestoreFailedName);

    /// <summary>
    /// The names of what an update replaces, in <see cref="Folder"/>. Windows: the files of the zip, so that what the
    /// user added to the folder stays; macOS: the bundle.
    /// </summary>
    public IReadOnlyList<string> Items => Platform == UpdatePlatform.WindowsX64
        ? ["ShowTime.exe", "LISEZ-MOI.txt", "app"]
        : [AppPath[(AppPath.LastIndexOf('/') + 1)..]];

    /// <summary>The staging folders and the backup an interrupted update left behind.</summary>
    public IEnumerable<string> Leftovers(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var folder = Folder;
        if (!fileSystem.Directory.Exists(folder))
            return [];
        return fileSystem.Directory.EnumerateDirectories(folder)
            .Select(path => fileSystem.Path.GetFileName(path))
            .Where(name => name == BackupName
                || (name is not null && name.StartsWith(StagingPrefix, StringComparison.Ordinal)
                    && SemanticVersion.TryParse(name[StagingPrefix.Length..], out _)))
            .Select(name => Join(name!))
            .ToList();
    }

    private string Join(string name)
    {
        var folder = Folder;
        return folder.EndsWith(Separator) ? folder + name : folder + Separator + name;
    }
}
