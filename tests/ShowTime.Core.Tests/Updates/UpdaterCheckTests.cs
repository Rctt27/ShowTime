using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

/// <summary>When ShowTime asks GitHub for the latest release, and what it makes of the answer.</summary>
public sealed class UpdaterCheckTests : IDisposable
{
    private static readonly SemanticVersion V063 = new(0, 6, 3);

    private readonly AppSettings _settings = new(SettingsDefaults.ForWindows(@"C:\Out"));
    private readonly FakeReleaseFeed _feed = new();
    private readonly IUserDialogs _dialogs = Substitute.For<IUserDialogs>();
    private readonly FakeTimeProvider _clock = new();
    private readonly ListLogger<Updater> _log = new();
    private readonly List<Updater> _updaters = [];

    public void Dispose()
    {
        foreach (var updater in _updaters)
            updater.Dispose();
    }

    private Updater Create(string current = "0.6.2", IUiThread? uiThread = null)
    {
        SemanticVersion.TryParse(current, out var version).ShouldBeTrue();
        var fileSystem = new MockFileSystem();
        var updater = new Updater(
            _settings, _feed, new FakeUpdateDownloader(fileSystem), new FakeUpdateInstaller(), new FakeUpdateHost(),
            _dialogs, new FakeRecordingStatus(), uiThread ?? new ImmediateUiThread(), fileSystem,
            Substitute.For<IAppPaths>(), version, _clock, _log);
        _updaters.Add(updater);
        return updater;
    }

    private Updater Started(string current = "0.6.2")
    {
        var updater = Create(current);
        updater.Start();
        return updater;
    }

    [Fact]
    public void The_first_check_runs_5_seconds_after_start()
    {
        Started();

        _clock.Advance(TimeSpan.FromSeconds(4.9));
        _feed.Calls.ShouldBe(0);

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        _feed.Calls.ShouldBe(1);
    }

    [Fact]
    public void Checks_repeat_every_6_hours()
    {
        Started();
        _clock.Advance(Updater.StartupDelay);

        _clock.Advance(Updater.Interval - TimeSpan.FromSeconds(1));
        _feed.Calls.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _feed.Calls.ShouldBe(2);

        _clock.Advance(Updater.Interval);
        _feed.Calls.ShouldBe(3);
    }

    [Fact]
    public void A_newer_release_becomes_available()
    {
        var updater = Started("0.6.2");
        var changes = 0;
        updater.Changed += (_, _) => changes++;

        _clock.Advance(Updater.StartupDelay);

        updater.State.Stage.ShouldBe(UpdateStage.Available);
        updater.State.Release.ShouldNotBeNull().Version.ShouldBe(V063);
        changes.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("0.6.3")]
    [InlineData("0.8.0")]
    public void An_older_or_equal_release_offers_nothing(string current)
    {
        var updater = Started(current);

        _clock.Advance(Updater.StartupDelay);

        _feed.Calls.ShouldBe(1);
        updater.State.ShouldBe(new UpdateState(UpdateStage.Idle, null, 0));
    }

    [Fact]
    public void An_unusable_release_offers_nothing()
    {
        // A pre-release is a valid answer: nothing to offer, and no need to ask again for 15 minutes.
        var json = System.Text.Json.Nodes.JsonNode.Parse(UpdateFixtures.Release063)!.AsObject();
        json["prerelease"] = true;
        _feed.Json = json.ToJsonString();
        var updater = Started();

        _clock.Advance(Updater.StartupDelay);
        updater.MenuOpening();

        _feed.Calls.ShouldBe(1);
        updater.State.Stage.ShouldBe(UpdateStage.Idle);
        _log.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void An_unreadable_answer_is_a_failure_and_retries_on_next_menu_opening()
    {
        _feed.Json = "<html>Unicorn!</html>";
        var updater = Started();

        _clock.Advance(Updater.StartupDelay);

        updater.State.Stage.ShouldBe(UpdateStage.Idle);
        _dialogs.ReceivedCalls().ShouldBeEmpty();
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning);

        _feed.Json = UpdateFixtures.Release063;
        _clock.Advance(TimeSpan.FromMinutes(1));
        updater.MenuOpening();

        _feed.Calls.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public void An_unreadable_answer_keeps_a_version_already_offered()
    {
        var updater = Started();
        _clock.Advance(Updater.StartupDelay);

        _feed.Json = "{}";
        _clock.Advance(Updater.Interval);

        _feed.Calls.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public void A_manual_check_reports_an_unreadable_answer()
    {
        _settings.CheckForUpdates = false;
        _feed.Json = "{ not json";
        var updater = Create("0.6.3");

        updater.CheckNowAsync().IsCompletedSuccessfully.ShouldBeTrue();

        _dialogs.Received(1).ShowError("Impossible de vérifier les mises à jour : la réponse de GitHub est illisible.");
        _dialogs.DidNotReceiveWithAnyArgs().ShowInfo(default!);
    }

    [Fact]
    public void Opening_the_menu_checks_only_when_the_answer_is_older_than_15_minutes()
    {
        var updater = Started("0.6.3");
        _clock.Advance(Updater.StartupDelay);

        _clock.Advance(TimeSpan.FromMinutes(14));
        updater.MenuOpening();
        _feed.Calls.ShouldBe(1);

        _clock.Advance(TimeSpan.FromMinutes(1));
        updater.MenuOpening();
        _feed.Calls.ShouldBe(2);
    }

    [Fact]
    public void Opening_the_menu_before_any_answer_checks()
    {
        var updater = Started();

        updater.MenuOpening();

        _feed.Calls.ShouldBe(1);
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public void Only_one_check_runs_at_a_time()
    {
        _feed.Pending = new TaskCompletionSource<string>();
        var updater = Started();

        updater.MenuOpening();
        updater.MenuOpening();
        _clock.Advance(Updater.StartupDelay);
        var manual = updater.CheckNowAsync();

        _feed.Calls.ShouldBe(1);
        updater.State.Stage.ShouldBe(UpdateStage.Checking);

        _feed.Pending.SetResult(UpdateFixtures.Release063);

        manual.IsCompleted.ShouldBeTrue();
        updater.State.Stage.ShouldBe(UpdateStage.Available);
        _feed.Pending = null;
        _clock.Advance(Updater.Freshness);
        updater.MenuOpening();
        _feed.Calls.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void An_automatic_check_that_fails_shows_nothing_and_retries_on_next_menu_opening(Exception failure)
    {
        _feed.Failure = failure;
        var updater = Started();

        _clock.Advance(Updater.StartupDelay);

        _feed.Calls.ShouldBe(1);
        updater.State.Stage.ShouldBe(UpdateStage.Idle);
        _dialogs.ReceivedCalls().ShouldBeEmpty();
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning);

        _feed.Failure = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        updater.MenuOpening();

        _feed.Calls.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    public static TheoryData<Exception> Failures() =>
        [new HttpRequestException("No such host is known."), new TaskCanceledException("The request timed out.")];

    [Fact]
    public void A_failed_check_keeps_a_version_already_offered()
    {
        var updater = Started();
        _clock.Advance(Updater.StartupDelay);

        _feed.Failure = new HttpRequestException("offline");
        _clock.Advance(Updater.Interval);

        _feed.Calls.ShouldBe(2);
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public void Nothing_is_checked_automatically_when_the_setting_is_off()
    {
        _settings.CheckForUpdates = false;
        var updater = Started();

        _clock.Advance(Updater.StartupDelay);
        _clock.Advance(Updater.Interval);
        updater.MenuOpening();

        _feed.Calls.ShouldBe(0);
    }

    [Fact]
    public void Ticking_the_setting_again_resumes_the_checks()
    {
        _settings.CheckForUpdates = false;
        Started();
        _clock.Advance(Updater.StartupDelay);

        _settings.CheckForUpdates = true;
        _clock.Advance(Updater.Interval);

        _feed.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_manual_check_says_when_ShowTime_is_up_to_date()
    {
        _settings.CheckForUpdates = false;
        var updater = Create("0.6.3");

        updater.CheckNowAsync().IsCompletedSuccessfully.ShouldBeTrue();

        _dialogs.Received(1).ShowInfo("ShowTime est à jour (version 0.6.3).");
        updater.State.Stage.ShouldBe(UpdateStage.Idle);
    }

    [Fact]
    public void A_manual_check_that_finds_a_version_offers_it_without_a_dialog()
    {
        _settings.CheckForUpdates = false;
        var updater = Create("0.6.2");

        updater.CheckNowAsync().IsCompletedSuccessfully.ShouldBeTrue();

        updater.State.Stage.ShouldBe(UpdateStage.Available);
        _dialogs.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_manual_check_reports_its_failure(Exception failure)
    {
        _settings.CheckForUpdates = false;
        _feed.Failure = failure;
        var updater = Create();

        updater.CheckNowAsync().IsCompletedSuccessfully.ShouldBeTrue();

        _dialogs.Received(1).ShowError(Arg.Is<string>(m =>
            m.StartsWith("Impossible de vérifier les mises à jour : ", StringComparison.Ordinal)
            && m.Length > "Impossible de vérifier les mises à jour : ".Length));
        updater.State.Stage.ShouldBe(UpdateStage.Idle);
    }

    [Fact]
    public void A_manual_check_names_the_status_GitHub_answered()
    {
        // GitHub answers 403 once the unauthenticated rate limit is reached.
        _feed.Failure = new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);
        var updater = Create();

        updater.CheckNowAsync().IsCompletedSuccessfully.ShouldBeTrue();

        _dialogs.Received(1).ShowError("Impossible de vérifier les mises à jour : GitHub a répondu 403.");
    }

    [Fact]
    public void A_manual_check_shows_that_it_is_searching()
    {
        _feed.Pending = new TaskCompletionSource<string>();
        var updater = Create();
        var stages = new List<UpdateStage>();
        updater.Changed += (_, _) => stages.Add(updater.State.Stage);

        var check = updater.CheckNowAsync();
        _feed.Pending.SetResult(UpdateFixtures.Release063);

        check.IsCompleted.ShouldBeTrue();
        stages.ShouldBe([UpdateStage.Checking, UpdateStage.Available]);
    }

    [Fact]
    public void The_answer_is_applied_on_the_UI_thread()
    {
        var ui = new ManualUiThread();
        _feed.Pending = new TaskCompletionSource<string>();
        var updater = Create(uiThread: ui);
        updater.Start();

        _clock.Advance(Updater.StartupDelay);
        _feed.Calls.ShouldBe(0);
        ui.RunAll();
        _feed.Calls.ShouldBe(1);

        _feed.Pending.SetResult(UpdateFixtures.Release063);
        updater.State.Stage.ShouldBe(UpdateStage.Checking);
        ui.Pending.ShouldBe(1);

        ui.RunAll();
        updater.State.Stage.ShouldBe(UpdateStage.Available);
    }

    [Fact]
    public void Dispose_stops_the_checks()
    {
        var updater = Started();

        updater.Dispose();
        _clock.Advance(Updater.StartupDelay);
        _clock.Advance(Updater.Interval);

        _feed.Calls.ShouldBe(0);
    }

    [Fact]
    public void An_answer_arriving_after_Dispose_is_ignored()
    {
        _feed.Pending = new TaskCompletionSource<string>();
        var updater = Started();
        _clock.Advance(Updater.StartupDelay);
        var changes = 0;
        updater.Changed += (_, _) => changes++;

        updater.Dispose();
        _feed.Pending.SetResult(UpdateFixtures.Release063);

        changes.ShouldBe(0);
    }

    [Fact]
    public void The_check_uses_the_installers_platform()
    {
        // The fixture's macOS asset is the only one: a Windows install finds nothing to offer.
        var json = System.Text.Json.Nodes.JsonNode.Parse(UpdateFixtures.Release063)!.AsObject();
        var assets = json["assets"]!.AsArray();
        assets.RemoveAt(1);
        _feed.Json = json.ToJsonString();
        var updater = Started();

        _clock.Advance(Updater.StartupDelay);

        updater.State.Stage.ShouldBe(UpdateStage.Idle);
    }
}
