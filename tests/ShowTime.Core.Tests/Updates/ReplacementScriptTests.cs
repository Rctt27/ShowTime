using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class ReplacementScriptTests
{
    private static readonly SemanticVersion Current = new(0, 6, 3);
    private static readonly SemanticVersion Next = new(0, 7, 0);

    private static ReplacementPlan WindowsPlan(string install = @"C:\Tools\ShowTime") =>
        ReplacementPlan.For(4242, new UpdateLayout(UpdatePlatform.WindowsX64, install),
            install + @"\.update-0.7.0\extracted", Current, Next, @"C:\Users\Zoé\AppData\Local\ShowTime\Logs\update.log");

    private static ReplacementPlan MacPlan(string bundle = "/Applications/ShowTime.app", string log = "/Users/zoé/Library/Logs/ShowTime/update.log") =>
        ReplacementPlan.For(4242, new UpdateLayout(UpdatePlatform.MacOS, bundle),
            "/Applications/.ShowTime-update-0.7.0/extracted/ShowTime.app", Current, Next, log);

    [Fact]
    public void The_Windows_plan_relaunches_the_exe_with_the_confirmation_or_the_failure()
    {
        var plan = WindowsPlan();

        plan.ProcessId.ShouldBe(4242);
        plan.StagedApp.ShouldBe(@"C:\Tools\ShowTime\.update-0.7.0\extracted");
        plan.ConfirmFile.ShouldBe(@"C:\Tools\ShowTime\.update-0.7.0\confirmed");
        plan.ConfirmTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        plan.LaunchNew.ShouldBe([@"C:\Tools\ShowTime\ShowTime.exe", "--update-confirm", @"C:\Tools\ShowTime\.update-0.7.0\confirmed"]);
        plan.LaunchOld.ShouldBe([@"C:\Tools\ShowTime\ShowTime.exe", "--update-failed", "0.7.0"]);
    }

    [Fact]
    public void The_macOS_plan_relaunches_the_bundle_with_open()
    {
        var plan = MacPlan();

        plan.ConfirmFile.ShouldBe("/Applications/.ShowTime-update-0.7.0/confirmed");
        plan.ConfirmTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        plan.LaunchNew.ShouldBe(["open", "-n", "/Applications/ShowTime.app", "--args", "--update-confirm", "/Applications/.ShowTime-update-0.7.0/confirmed"]);
        plan.LaunchOld.ShouldBe(["open", "-n", "/Applications/ShowTime.app", "--args", "--update-failed", "0.7.0"]);
    }

    [Fact]
    public void The_Windows_script_holds_the_process_the_timeout_the_launches_and_the_log()
    {
        var script = ReplacementScript.ForWindows(WindowsPlan());

        script.ShouldContain("$processId = 4242", Case.Sensitive);
        script.ShouldContain("$confirmSeconds = 60", Case.Sensitive);
        script.ShouldContain(@"$install = 'C:\Tools\ShowTime'", Case.Sensitive);
        script.ShouldContain(@"$backup = 'C:\Tools\ShowTime\.old'", Case.Sensitive);
        script.ShouldContain(@"$staged = 'C:\Tools\ShowTime\.update-0.7.0\extracted'", Case.Sensitive);
        script.ShouldContain(@"$staging = 'C:\Tools\ShowTime\.update-0.7.0'", Case.Sensitive);
        script.ShouldContain("$items = @('ShowTime.exe', 'LISEZ-MOI.txt', 'app')", Case.Sensitive);
        script.ShouldContain(@"$confirm = 'C:\Tools\ShowTime\.update-0.7.0\confirmed'", Case.Sensitive);
        script.ShouldContain(@"$newFile = 'C:\Tools\ShowTime\ShowTime.exe'", Case.Sensitive);
        script.ShouldContain(@"$newArguments = '--update-confirm C:\Tools\ShowTime\.update-0.7.0\confirmed'", Case.Sensitive);
        script.ShouldContain(@"$oldFile = 'C:\Tools\ShowTime\ShowTime.exe'", Case.Sensitive);
        script.ShouldContain("$oldArguments = '--update-failed 0.7.0'", Case.Sensitive);
        script.ShouldContain(@"$log = 'C:\Users\Zoé\AppData\Local\ShowTime\Logs\update.log'", Case.Sensitive);
        script.ShouldContain("Get-Process -Name 'ShowTime'", Case.Sensitive);
    }

    [Fact]
    public void The_macOS_script_holds_the_process_the_timeout_the_launches_and_the_log()
    {
        var script = ReplacementScript.ForMacOS(MacPlan());

        script.ShouldStartWith("#!/bin/sh\n", Case.Sensitive);
        script.ShouldNotContain("\r", Case.Sensitive);
        script.ShouldContain("pid=4242", Case.Sensitive);
        script.ShouldContain("confirm_seconds=60", Case.Sensitive);
        script.ShouldContain("bundle='/Applications/ShowTime.app'", Case.Sensitive);
        script.ShouldContain("backup='/Applications/.ShowTime-old.app'", Case.Sensitive);
        script.ShouldContain("staged='/Applications/.ShowTime-update-0.7.0/extracted/ShowTime.app'", Case.Sensitive);
        script.ShouldContain("staging='/Applications/.ShowTime-update-0.7.0'", Case.Sensitive);
        script.ShouldContain("confirm='/Applications/.ShowTime-update-0.7.0/confirmed'", Case.Sensitive);
        script.ShouldContain("'open' '-n' '/Applications/ShowTime.app' '--args' '--update-confirm' '/Applications/.ShowTime-update-0.7.0/confirmed'", Case.Sensitive);
        script.ShouldContain("'open' '-n' '/Applications/ShowTime.app' '--args' '--update-failed' '0.7.0'", Case.Sensitive);
        script.ShouldContain("log='/Users/zoé/Library/Logs/ShowTime/update.log'", Case.Sensitive);
        script.ShouldContain(@"pkill -f '/Applications/ShowTime\.app/Contents/MacOS/ShowTime'", Case.Sensitive);
    }

    [Fact]
    public void Scripts_quote_paths_with_spaces_apostrophes_and_accents()
    {
        var windows = ReplacementScript.ForWindows(WindowsPlan(@"C:\Users\Zoé\Mes outils\ShowTime d'été"));
        var mac = ReplacementScript.ForMacOS(MacPlan("/Users/zoé/Applications/Show Time.app", "/Users/zoé/Library/Logs/l'app/update.log"));

        windows.ShouldContain(@"$install = 'C:\Users\Zoé\Mes outils\ShowTime d''été'", Case.Sensitive);
        // The arguments are one command line, with Windows' quotes around a path with spaces.
        windows.ShouldContain(@"$newArguments = '--update-confirm ""C:\Users\Zoé\Mes outils\ShowTime d''été\.update-0.7.0\confirmed""'", Case.Sensitive);
        mac.ShouldContain("bundle='/Users/zoé/Applications/Show Time.app'", Case.Sensitive);
        mac.ShouldContain("log='/Users/zoé/Library/Logs/l'\\''app/update.log'", Case.Sensitive);
        mac.ShouldContain("pkill -f '/Users/zoé/Applications/Show Time\\.app/Contents/MacOS/ShowTime'", Case.Sensitive);
    }

    [Fact]
    public void The_Windows_script_doubles_typographic_apostrophes_too()
    {
        // PowerShell ends a single-quoted string at ’ as well as at '.
        var script = ReplacementScript.ForWindows(WindowsPlan(@"C:\Outils de l’équipe\ShowTime"));

        script.ShouldContain(@"$install = 'C:\Outils de l’’équipe\ShowTime'", Case.Sensitive);
    }

    [Fact]
    public void Windows_arguments_follow_the_command_line_rules()
    {
        var plan = WindowsPlan() with { LaunchNew = ["tool.exe", "plain", "two words", "", "say \"hi\"", @"trailing\", @"C:\a b\"] };

        var script = ReplacementScript.ForWindows(plan);

        script.ShouldContain(@"$newArguments = 'plain ""two words"" """" ""say \""hi\"""" trailing\ ""C:\a b\\""'", Case.Sensitive);
    }

    [Fact]
    public void The_scripts_use_the_plans_timeout()
    {
        var windows = ReplacementScript.ForWindows(WindowsPlan() with { ConfirmTimeout = TimeSpan.FromSeconds(3) });
        var mac = ReplacementScript.ForMacOS(MacPlan() with { ConfirmTimeout = TimeSpan.FromMilliseconds(2500) });

        windows.ShouldContain("$confirmSeconds = 3\r\n", Case.Sensitive);
        mac.ShouldContain("confirm_seconds=3\n", Case.Sensitive);
    }
}
