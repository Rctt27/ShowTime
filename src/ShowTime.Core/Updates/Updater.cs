using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Updates;

/// <summary>
/// Looks for a newer version, by itself (at startup, every 6 hours, on opening the menu once the last answer is stale)
/// or when asked, and installs it. Every member is used from the UI thread; <see cref="Changed"/> is raised there.
/// </summary>
public sealed class Updater(
    AppSettings settings,
    IReleaseFeed feed,
    IUpdateDownloader downloader,
    IUpdateInstaller installer,
    IUpdateHost host,
    IUserDialogs dialogs,
    IRecordingStatus recording,
    IUiThread uiThread,
    IFileSystem fileSystem,
    IAppPaths paths,
    SemanticVersion current,
    TimeProvider clock,
    ILogger<Updater> log) : IDisposable
{
    /// <summary>The first check waits this long, so as not to slow the launch down.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>Between two scheduled checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Opening the menu checks again once the last answer is this old.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    private const string CheckFailedMessage = "Impossible de vérifier les mises à jour : ";
    private const string DownloadFailedMessage = "La mise à jour n'a pas pu être téléchargée : ";
    private const string InstallFailedMessage = "La mise à jour n'a pas pu être installée : ";
    private const string ZipName = "ShowTime.zip";
    private const string ScriptLogName = "update.log";

    private readonly CancellationTokenSource _disposing = new();
    private ITimer? _schedule;
    private TaskCompletionSource<CheckResult>? _check;
    private DateTimeOffset? _lastAnswer;
    private string? _stagedApp;
    private bool _disposed;

    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Schedules the checks; each one is skipped while <see cref="AppSettings.CheckForUpdates"/> is off.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _schedule ??= clock.CreateTimer(_ => uiThread.Post(CheckAutomatically), null, StartupDelay, Interval);
    }

    /// <summary>The menu is opening: checks when automatic checks are on and the last answer is stale or failed.</summary>
    public void MenuOpening()
    {
        if (_lastAnswer is { } last && clock.GetUtcNow() - last < Freshness)
            return;
        CheckAutomatically();
    }

    /// <summary>The menu's "Rechercher une mise à jour": says when ShowTime is up to date, or why the check failed.</summary>
    public async Task CheckNowAsync()
    {
        if (_disposed || !CanCheck)
            return;

        var result = await Check();
        if (_disposed)
            return;
        if (result.Failure is { } failure)
            dialogs.ShowError(CheckFailedMessage + Reason(failure));
        else if (State.Stage == UpdateStage.Idle)
            dialogs.ShowInfo($"ShowTime est à jour (version {current}).");
    }

    /// <summary>
    /// The menu's « Installer la version X.Y.Z »: downloads, checks and stages the version offered, then, once no
    /// recording is under way and the windows have closed, starts the replacement and quits. From
    /// <see cref="UpdateStage.Downloaded"/>, installs what was staged without downloading again; does nothing otherwise.
    /// </summary>
    public async Task InstallAsync()
    {
        if (_disposed || State is not { Stage: UpdateStage.Available or UpdateStage.Downloaded, Release: { } release })
            return;

        var layout = installer.Layout;
        if (UpdateStartup.KeptBackup(layout, fileSystem, log) is { } backup)
        {
            // The backup may be the only copy of the old version left: a new update would replace it.
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning("Update to {Version} refused: an earlier one could not be undone, see {Backup}", release.Version, backup);
            dialogs.ShowWarning(
                $"Une mise à jour précédente n'a pas pu être annulée entièrement : l'ancienne version est conservée dans {backup}. Déplacez ou supprimez ce dossier avant de mettre à jour.");
            return;
        }

        if (!installer.CanReplaceApp())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Cannot replace the app in {Folder}; opening the page of version {Version}", layout.AppPath, release.Version);
            dialogs.ShowInfo(
                $"ShowTime ne peut pas se remplacer dans ce dossier : la page de la version {release.Version} s'ouvre dans le navigateur.");
            host.OpenInBrowser(release.PageUrl);
            return;
        }

        if (State.Stage == UpdateStage.Available)
        {
            // DownloadAsync ends on the UI thread, so this goes on there.
            _stagedApp = await DownloadAsync(release).ConfigureAwait(false);
            if (_stagedApp is null)
                return;
        }

        InstallWhenIdle();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _schedule?.Dispose();
        recording.StateChanged -= OnRecordingChanged;

        // A download under way deletes its staging folder once cancelled; a staged version waiting to be installed is
        // deleted here. Once installing, the replacement script needs it.
        if (State is { Stage: UpdateStage.WaitingForRecording or UpdateStage.Downloaded, Release: { } release })
            DeleteStaging(installer.Layout.StagingFolder(release.Version));
        _disposing.Cancel();
        _disposing.Dispose();
    }

    /// <summary>A check changes nothing once an install has started.</summary>
    private bool CanCheck => State.Stage is UpdateStage.Idle or UpdateStage.Checking or UpdateStage.Available;

    private void CheckAutomatically()
    {
        if (!_disposed && settings.CheckForUpdates && CanCheck)
            _ = Check();
    }

    /// <summary>Starts a check, or joins the one under way.</summary>
    private Task<CheckResult> Check()
    {
        if (_check is not null)
            return _check.Task;

        var check = new TaskCompletionSource<CheckResult>();
        _check = check;
        if (State.Stage == UpdateStage.Idle)
            SetState(State with { Stage = UpdateStage.Checking });
        _ = FetchAsync(check);
        return check.Task;
    }

    private async Task FetchAsync(TaskCompletionSource<CheckResult> check)
    {
        CheckResult result;
        try
        {
            var json = await feed.GetLatestAsync(_disposing.Token).ConfigureAwait(false);
            result = new CheckResult(GitHubRelease.Parse(json, installer.Platform), null);
        }
        catch (Exception ex)
        {
            // HttpRequestException, the feed's timeout, or an answer that is not a release (JsonException);
            // whatever it is, the next check tries again.
            result = new CheckResult(null, ex);
        }

        uiThread.Post(() =>
        {
            _check = null;
            if (!_disposed)
                Apply(result);
            check.SetResult(result);
        });
    }

    private void Apply(CheckResult result)
    {
        if (result.Failure is { } failure)
        {
            _lastAnswer = null;
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning(failure, "Update check failed");
            if (State.Stage == UpdateStage.Checking)
                SetState(UpdateState.Idle);
            return;
        }

        _lastAnswer = clock.GetUtcNow();
        var offered = result.Release is { } release && release.Version > current ? release : null;
        if (log.IsEnabled(LogLevel.Information))
        {
            log.LogInformation(
                "Update check: latest release {Latest}, running {Current}",
                result.Release?.Version.ToString() ?? "none usable", current);
        }

        if (CanCheck)
            SetState(offered is null ? UpdateState.Idle : new UpdateState(UpdateStage.Available, offered, 0));
    }

    /// <summary>
    /// Downloads, checks and stages <paramref name="release"/>; returns the staged app, or null when that failed (said to
    /// the user, staging folder deleted, the version offered again) or ShowTime is quitting. Ends on the UI thread.
    /// </summary>
    private async Task<string?> DownloadAsync(ReleaseInfo release)
    {
        var staging = installer.Layout.StagingFolder(release.Version);
        var cancellation = _disposing.Token;
        SetState(new UpdateState(UpdateStage.Downloading, release, 0));
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Downloading version {Version} from {Url}", release.Version, release.DownloadUrl);

        string? staged = null;
        Exception? failure = null;
        try
        {
            staged = await DownloadAndStageAsync(release, staging, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A network or disk failure, a file that is not the one announced, a staging the installer refused, or
            // Dispose: whatever it is, nothing half-done is kept.
            failure = ex;
            DeleteStaging(staging);
        }

        await OnUiThread().ConfigureAwait(false);
        if (_disposed)
            return null;
        if (failure is not null)
        {
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning(failure, "Could not download version {Version}", release.Version);
            SetState(new UpdateState(UpdateStage.Available, release, 0));
            dialogs.ShowError(DownloadFailedMessage + Reason(failure));
            return null;
        }

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Version {Version} downloaded, checked and staged in {Staging}", release.Version, staging);
        return staged;
    }

    /// <summary>Runs off the UI thread once the download has started: the digest of a 200 MB file takes a moment.</summary>
    private async Task<string> DownloadAndStageAsync(ReleaseInfo release, string staging, CancellationToken cancellation)
    {
        // A folder left by an attempt that could not clean up would mix its files with the new ones.
        DeleteStaging(staging);
        fileSystem.Directory.CreateDirectory(staging);
        var zip = fileSystem.Path.Combine(staging, ZipName);

        await downloader.DownloadAsync(release.DownloadUrl, zip, new PostedProgress(uiThread, ShowProgress), cancellation)
            .ConfigureAwait(false);
        if (!DownloadCheck.Matches(fileSystem, zip, release.Size, release.Sha256))
            throw new UpdateException("le fichier téléchargé est incomplet ou altéré");

        var staged = await installer.StageAsync(zip, staging, cancellation).ConfigureAwait(false);
        fileSystem.File.Delete(zip);
        cancellation.ThrowIfCancellationRequested();
        return staged;
    }

    private void ShowProgress(double progress)
    {
        // The menu shows whole percents: a 200 MB download would otherwise redraw it thousands of times.
        if (!_disposed && State.Stage == UpdateStage.Downloading
            && Math.Floor(progress * 100) != Math.Floor(State.Progress * 100))
            SetState(State with { Progress = progress });
    }

    /// <summary>Installs what was staged once no recording is under way, closing the windows first.</summary>
    private void InstallWhenIdle()
    {
        var release = State.Release!;
        if (recording.IsBusy)
        {
            if (State.Stage != UpdateStage.WaitingForRecording)
            {
                recording.StateChanged += OnRecordingChanged;
                if (log.IsEnabled(LogLevel.Information))
                    log.LogInformation("Version {Version} will be installed once the recording has ended", release.Version);
                SetState(State with { Stage = UpdateStage.WaitingForRecording });
            }

            return;
        }

        recording.StateChanged -= OnRecordingChanged;

        // Installing already while the windows ask about unsaved work: their questions let the menu be clicked again.
        SetState(State with { Stage = UpdateStage.Installing });
        if (!host.TryCloseWindows())
        {
            // An editor the user chose to keep: the next click installs what is staged.
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Install of version {Version} put off: a window was kept open", release.Version);
            SetState(State with { Stage = UpdateStage.Downloaded });
            return;
        }

        // The questions pumped messages: ShowTime may have been quit, or a recording started by a shortcut meanwhile.
        if (_disposed)
            return;
        if (recording.IsBusy)
        {
            SetState(State with { Stage = UpdateStage.Downloaded });
            InstallWhenIdle();
            return;
        }

        var plan = ReplacementPlan.For(Environment.ProcessId, installer.Layout, _stagedApp!, current, release.Version,
            fileSystem.Path.Combine(paths.LogsFolder, ScriptLogName));
        try
        {
            installer.StartReplacement(plan);
        }
        catch (Exception ex)
        {
            // The script did not start: ShowTime keeps running, and the staged version stays for another click.
            log.LogError(ex, "Could not start the replacement by version {Version}", release.Version);
            SetState(State with { Stage = UpdateStage.Downloaded });
            dialogs.ShowError(InstallFailedMessage + Reason(ex));
            return;
        }

        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Replacing version {Current} with {Version}; quitting", current, release.Version);
        host.Quit();
    }

    /// <summary>The recording session may raise this mid-transition: the install goes on outside its handlers.</summary>
    private void OnRecordingChanged(object? sender, EventArgs e) => uiThread.Post(() =>
    {
        if (!_disposed && State.Stage == UpdateStage.WaitingForRecording && !recording.IsBusy)
            InstallWhenIdle();
    });

    private void DeleteStaging(string staging)
    {
        try
        {
            if (fileSystem.Directory.Exists(staging))
                fileSystem.Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start deletes it with what other updates left.
            log.LogWarning(ex, "Could not delete {Staging}", staging);
        }
    }

    /// <summary>Completes on the UI thread: awaited with <c>ConfigureAwait(false)</c>, what follows runs there.</summary>
    private Task OnUiThread()
    {
        var reached = new TaskCompletionSource();
        uiThread.Post(reached.SetResult);
        return reached.Task;
    }

    private void SetState(UpdateState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Reason(Exception failure) => failure switch
    {
        OperationCanceledException => "GitHub n'a pas répondu à temps.",
        HttpRequestException { StatusCode: { } status } => $"GitHub a répondu {(int)status}.",
        HttpRequestException => "GitHub est injoignable. Vérifiez la connexion à Internet.",
        JsonException => "la réponse de GitHub est illisible.",
        _ => failure.Message,
    };

    private sealed record CheckResult(ReleaseInfo? Release, Exception? Failure);

    /// <summary>Reports the downloader's progress on the UI thread.</summary>
    private sealed class PostedProgress(IUiThread uiThread, Action<double> show) : IProgress<double>
    {
        public void Report(double value) => uiThread.Post(() => show(value));
    }
}
