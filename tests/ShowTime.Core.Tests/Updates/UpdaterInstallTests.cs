using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

/// <summary>From the click on « Installer la version X.Y.Z » to ShowTime quitting for the replacement script.</summary>
public sealed class UpdaterInstallTests : IDisposable
{
    private static readonly SemanticVersion V062 = new(0, 6, 2);
    private static readonly SemanticVersion V063 = new(0, 6, 3);
    private static readonly byte[] Zip = "the zip of ShowTime 0.6.3"u8.ToArray();
    private const string DownloadFailed = "La mise à jour n'a pas pu être téléchargée : ";

    private readonly AppSettings _settings = new(SettingsDefaults.ForWindows(@"C:\Out"));
    private readonly MockFileSystem _fs = new();
    private readonly FakeReleaseFeed _feed = new();
    private readonly FakeUpdateDownloader _downloader;
    private readonly FakeUpdateInstaller _installer = new();
    private readonly FakeUpdateHost _host = new();
    private readonly IUserDialogs _dialogs = Substitute.For<IUserDialogs>();
    private readonly FakeRecordingStatus _recording = new();
    private readonly IAppPaths _paths = Substitute.For<IAppPaths>();
    private readonly FakeTimeProvider _clock = new();
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "ShowTimeLogs");
    private readonly List<Updater> _updaters = [];

    public UpdaterInstallTests()
    {
        // The host's own paths, so that the mock file system handles them on every OS.
        var install = Path.Combine(Path.GetTempPath(), "ShowTimeInstall");
        var platform = OperatingSystem.IsWindows() ? UpdatePlatform.WindowsX64 : UpdatePlatform.MacOS;
        _installer.Platform = platform;
        _installer.Layout = new UpdateLayout(platform,
            platform == UpdatePlatform.WindowsX64 ? install : Path.Combine(install, "ShowTime.app"));
        _fs.AddDirectory(_installer.Layout.AppPath);
        _downloader = new FakeUpdateDownloader(_fs) { Content = Zip };
        _paths.LogsFolder.Returns(_logs);
        _feed.Json = ReleaseOf(Zip);
    }

    public void Dispose()
    {
        foreach (var updater in _updaters)
            updater.Dispose();
    }

    private string Staging => _installer.Layout.StagingFolder(V063);

    private string StagedZip => Path.Combine(Staging, "ShowTime.zip");

    /// <summary>The v0.6.3 release, with assets of the size and digest of <paramref name="zip"/>.</summary>
    private static string ReleaseOf(byte[] zip)
    {
        var json = JsonNode.Parse(UpdateFixtures.Release063)!.AsObject();
        foreach (var asset in json["assets"]!.AsArray())
        {
            asset!["size"] = zip.Length;
            asset["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(zip));
        }

        return json.ToJsonString();
    }

    private Updater Create(IUiThread? uiThread = null)
    {
        var updater = new Updater(
            _settings, _feed, _downloader, _installer, _host, _dialogs, _recording, uiThread ?? new ImmediateUiThread(),
            _fs, _paths, V062, _clock, new ListLogger<Updater>());
        _updaters.Add(updater);
        return updater;
    }

    /// <summary>An updater offering version 0.6.3.</summary>
    private async Task<Updater> Offering()
    {
        var updater = Create();
        await updater.CheckNowAsync();
        updater.State.Stage.ShouldBe(UpdateStage.Available);
        _dialogs.ClearReceivedCalls();
        return updater;
    }

    [Fact]
    public async Task Install_downloads_stages_replaces_and_quits()
    {
        var journal = new List<string>();
        _downloader.Journal = journal;
        _installer.Journal = journal;
        _host.Journal = journal;
        var updater = await Offering();
        var release = updater.State.Release!;
        var stages = new List<UpdateStage>();
        updater.Changed += (_, _) => stages.Add(updater.State.Stage);

        await updater.InstallAsync();

        journal.ShouldBe(["Download", "Stage", "TryCloseWindows", "StartReplacement", "Quit"]);
        stages.ShouldBe([UpdateStage.Downloading, UpdateStage.Installing]);
        _downloader.Calls.ShouldBe([(release.DownloadUrl, StagedZip)]);
        _installer.Staged.ShouldBe([(StagedZip, Staging)]);
        _fs.File.Exists(StagedZip).ShouldBeFalse();
        var plan = _installer.Replacements.ShouldHaveSingleItem();
        var expected = ReplacementPlan.For(Environment.ProcessId, _installer.Layout, Path.Combine(Staging, "ShowTime"),
            V062, V063, Path.Combine(_logs, "update.log"));
        plan.ShouldBe(expected with { LaunchNew = plan.LaunchNew, LaunchOld = plan.LaunchOld });
        plan.LaunchNew.ShouldBe(expected.LaunchNew);
        plan.LaunchOld.ShouldBe(expected.LaunchOld);
        _dialogs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Download_progress_is_reported()
    {
        _downloader.Progress = [0.5];
        _downloader.Pending = new TaskCompletionSource();
        var updater = await Offering();

        var install = updater.InstallAsync();

        updater.State.Stage.ShouldBe(UpdateStage.Downloading);
        updater.State.Progress.ShouldBe(0.5);
        _downloader.Pending.SetResult();
        await install;
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
    }

    [Fact]
    public async Task Progress_is_shown_on_the_UI_thread()
    {
        var ui = new ManualUiThread();
        _downloader.Progress = [0.25];
        _downloader.Pending = new TaskCompletionSource();
        var updater = Create(ui);
        var check = updater.CheckNowAsync();
        ui.RunAll();
        await check;

        var install = updater.InstallAsync();
        updater.State.Progress.ShouldBe(0);
        ui.RunAll();
        updater.State.Progress.ShouldBe(0.25);

        _downloader.Pending.SetResult();
        install.IsCompleted.ShouldBeFalse();
        updater.State.Stage.ShouldBe(UpdateStage.Downloading);
        ui.RunAll();
        await install;
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
    }

    [Fact]
    public async Task A_corrupt_download_is_refused()
    {
        _downloader.Content = "something else"u8.ToArray();
        var updater = await Offering();

        await updater.InstallAsync();

        _dialogs.Received(1).ShowError(DownloadFailed + "le fichier téléchargé est incomplet ou altéré");
        _fs.Directory.Exists(Staging).ShouldBeFalse();
        updater.State.ShouldBe(new UpdateState(UpdateStage.Available, updater.State.Release, 0));
        _installer.Staged.ShouldBeEmpty();
        _installer.Replacements.ShouldBeEmpty();
        _host.Quits.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_staging_is_reported()
    {
        _installer.StageFailure = new UpdateException("la signature de la nouvelle version ne correspond pas");
        var updater = await Offering();

        await updater.InstallAsync();

        _dialogs.Received(1).ShowError(Arg.Is<string>(m =>
            m.StartsWith(DownloadFailed, StringComparison.Ordinal) && m.Contains("signature", StringComparison.Ordinal)));
        _fs.Directory.Exists(Staging).ShouldBeFalse();
        updater.State.Stage.ShouldBe(UpdateStage.Available);
        _installer.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_download_is_reported_and_can_be_tried_again()
    {
        _downloader.Failure = new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
        var updater = await Offering();

        await updater.InstallAsync();

        _dialogs.Received(1).ShowError(DownloadFailed + "GitHub a répondu 404.");
        _fs.Directory.Exists(Staging).ShouldBeFalse();
        updater.State.Stage.ShouldBe(UpdateStage.Available);

        _downloader.Failure = null;
        await updater.InstallAsync();

        _downloader.Calls.Count.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
    }

    [Fact]
    public async Task A_staging_folder_left_by_an_earlier_attempt_is_replaced()
    {
        _fs.AddFile(Path.Combine(Staging, "ShowTime", "stale.txt"), new MockFileData("old"));
        var updater = await Offering();

        await updater.InstallAsync();

        _fs.File.Exists(Path.Combine(Staging, "ShowTime", "stale.txt")).ShouldBeFalse();
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
    }

    [Fact]
    public async Task An_unwritable_install_opens_the_release_page()
    {
        _installer.CanReplace = false;
        var updater = await Offering();

        await updater.InstallAsync();

        _dialogs.Received(1).ShowInfo(
            "ShowTime ne peut pas se remplacer dans ce dossier : la page de la version 0.6.3 s'ouvre dans le navigateur.");
        _host.Opened.ShouldBe([updater.State.Release!.PageUrl]);
        _downloader.Calls.ShouldBeEmpty();
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public async Task An_update_that_could_not_be_undone_blocks_the_next_one()
    {
        // The script wrote the backup's path in the marker: installing again would delete the only old copy.
        var backup = _installer.Layout.BackupPath;
        _fs.AddFile(_installer.Layout.RestoreFailedMarker, new MockFileData(backup + Environment.NewLine));
        var updater = await Offering();
        var before = updater.State;

        await updater.InstallAsync();

        _dialogs.Received(1).ShowWarning(
            $"Une mise à jour précédente n'a pas pu être annulée entièrement : l'ancienne version est conservée dans {backup}. Déplacez ou supprimez ce dossier avant de mettre à jour.");
        _downloader.Calls.ShouldBeEmpty();
        _host.Opened.ShouldBeEmpty();
        updater.State.ShouldBe(before);
    }

    [Fact]
    public async Task Install_waits_for_the_recording_to_end()
    {
        _recording.Set(true);
        var updater = await Offering();

        await updater.InstallAsync();

        updater.State.Stage.ShouldBe(UpdateStage.WaitingForRecording);
        _host.CloseRequests.ShouldBe(0);

        _recording.Set(true);
        updater.State.Stage.ShouldBe(UpdateStage.WaitingForRecording);

        _recording.Set(false);
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
        _installer.Replacements.ShouldHaveSingleItem();
        _host.Quits.ShouldBe(1);

        _recording.Set(true);
        _recording.Set(false);
        _host.Quits.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_an_unsaved_editor_keeps_the_download()
    {
        _host.CloseWindows = false;
        var updater = await Offering();

        await updater.InstallAsync();

        updater.State.Stage.ShouldBe(UpdateStage.Downloaded);
        _fs.Directory.Exists(Staging).ShouldBeTrue();
        _installer.Replacements.ShouldBeEmpty();
        _host.Quits.ShouldBe(0);

        _host.CloseWindows = true;
        await updater.InstallAsync();

        _downloader.Calls.ShouldHaveSingleItem();
        _installer.Staged.ShouldHaveSingleItem();
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
        _installer.Replacements.ShouldHaveSingleItem().StagedApp.ShouldBe(Path.Combine(Staging, "ShowTime"));
        _host.Quits.ShouldBe(1);
    }

    [Fact]
    public async Task A_click_while_an_editor_asks_about_unsaved_work_does_nothing()
    {
        _host.CloseWindows = false;
        var updater = await Offering();
        await updater.InstallAsync();
        Task? nested = null;
        _host.Closing = () =>
        {
            updater.State.Stage.ShouldBe(UpdateStage.Installing);
            nested ??= updater.InstallAsync();
        };

        await updater.InstallAsync();

        nested.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
        _host.CloseRequests.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Downloaded);
    }

    [Fact]
    public async Task A_replacement_that_cannot_start_keeps_the_download()
    {
        _installer.ReplacementFailure = new UpdateException("PowerShell est introuvable");
        var updater = await Offering();

        await updater.InstallAsync();

        _dialogs.Received(1).ShowError("La mise à jour n'a pas pu être installée : PowerShell est introuvable");
        updater.State.Stage.ShouldBe(UpdateStage.Downloaded);
        _fs.Directory.Exists(Staging).ShouldBeTrue();
        _host.Quits.ShouldBe(0);
    }

    [Fact]
    public async Task A_second_install_click_while_downloading_does_nothing()
    {
        _downloader.Pending = new TaskCompletionSource();
        var updater = await Offering();

        var first = updater.InstallAsync();
        var second = updater.InstallAsync();

        second.IsCompletedSuccessfully.ShouldBeTrue();
        _downloader.Calls.ShouldHaveSingleItem();
        _downloader.Pending.SetResult();
        await first;
        _downloader.Calls.ShouldHaveSingleItem();
        _installer.Replacements.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Install_does_nothing_without_a_version_offered()
    {
        var updater = Create();

        await updater.InstallAsync();

        _downloader.Calls.ShouldBeEmpty();
        updater.State.ShouldBe(UpdateState.Idle);
    }

    [Fact]
    public async Task Unticking_the_setting_does_not_stop_a_download()
    {
        _downloader.Pending = new TaskCompletionSource();
        var updater = await Offering();
        updater.Start();

        var install = updater.InstallAsync();
        _settings.CheckForUpdates = false;
        _clock.Advance(Updater.StartupDelay);
        _clock.Advance(Updater.Interval);
        _downloader.Pending.SetResult();
        await install;

        updater.State.Stage.ShouldBe(UpdateStage.Installing);
        _host.Quits.ShouldBe(1);
    }

    [Fact]
    public async Task A_check_answering_during_an_install_leaves_the_install_alone()
    {
        _downloader.Pending = new TaskCompletionSource();
        var updater = await Offering();
        _feed.Pending = new TaskCompletionSource<string>();
        _clock.Advance(Updater.Freshness);
        updater.MenuOpening();
        _feed.Calls.ShouldBe(2);

        var install = updater.InstallAsync();
        _feed.Pending.SetResult(ReleaseOf(Zip));

        updater.State.Stage.ShouldBe(UpdateStage.Downloading);
        _downloader.Pending.SetResult();
        await install;
        updater.State.Stage.ShouldBe(UpdateStage.Installing);
    }

    [Fact]
    public async Task Dispose_cancels_the_download_and_deletes_the_staging_folder()
    {
        _downloader.Pending = new TaskCompletionSource();
        var updater = await Offering();
        var install = updater.InstallAsync();
        _fs.Directory.Exists(Staging).ShouldBeTrue();

        updater.Dispose();
        await install;

        _fs.Directory.Exists(Staging).ShouldBeFalse();
        _installer.Staged.ShouldBeEmpty();
        _dialogs.ReceivedCalls().ShouldBeEmpty();
        _host.Quits.ShouldBe(0);
    }

    [Fact]
    public async Task Quitting_deletes_a_download_kept_for_later()
    {
        _host.CloseWindows = false;
        var updater = await Offering();
        await updater.InstallAsync();
        _fs.Directory.Exists(Staging).ShouldBeTrue();

        updater.Dispose();

        _fs.Directory.Exists(Staging).ShouldBeFalse();
    }

    [Fact]
    public async Task Quitting_to_install_keeps_the_staged_version_for_the_script()
    {
        var updater = await Offering();
        await updater.InstallAsync();

        updater.Dispose();

        _fs.Directory.Exists(Staging).ShouldBeTrue();
    }
}
