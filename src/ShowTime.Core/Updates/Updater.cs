using System.IO.Abstractions;
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

    private readonly CancellationTokenSource _disposing = new();
    private ITimer? _schedule;
    private TaskCompletionSource<CheckResult>? _check;
    private DateTimeOffset? _lastAnswer;
    private bool _disposed;

    // Used by the install, which comes next.
    private readonly IUpdateDownloader _downloader = downloader;
    private readonly IUpdateHost _host = host;
    private readonly IRecordingStatus _recording = recording;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IAppPaths _paths = paths;

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

    /// <summary>Downloads and installs the version offered.</summary>
    public Task InstallAsync() => throw new NotImplementedException();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _schedule?.Dispose();
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
            // HttpRequestException, or the feed's timeout; whatever it is, the next check tries again.
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
        _ => failure.Message,
    };

    private sealed record CheckResult(ReleaseInfo? Release, Exception? Failure);
}
