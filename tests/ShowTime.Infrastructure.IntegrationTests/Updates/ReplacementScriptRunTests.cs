using System.Diagnostics;
using ShowTime.Core.Updates;
using ShowTime.Infrastructure.Updates;

namespace ShowTime.Infrastructure.IntegrationTests.Updates;

/// <summary>
/// The replacement script run for real: PowerShell 5.1 on Windows, <c>/bin/sh</c> elsewhere (the macOS script), in an
/// install folder whose name has a space, an apostrophe and accents. The "old ShowTime" is a short-lived process, and
/// the versions the script starts are shell one-liners that write a file.
/// </summary>
public sealed class ReplacementScriptRunTests : IDisposable
{
    private static readonly SemanticVersion From = new(0, 6, 3);
    private static readonly SemanticVersion To = new(0, 7, 0);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(90);

    private readonly TempFolder _temp = new();
    private readonly bool _windows = OperatingSystem.IsWindows();
    private readonly string _folder;
    private readonly UpdateLayout _layout;
    private readonly string _staged;
    private readonly string _log;
    private readonly string _oldStarted;

    public ReplacementScriptRunTests()
    {
        _folder = Path.Combine(_temp.Path, "Show Time d'été");
        _log = Path.Combine(_temp.Path, "logs", "update.log");
        _oldStarted = Path.Combine(_temp.Path, "old started");
        if (_windows)
        {
            _layout = new UpdateLayout(UpdatePlatform.WindowsX64, _folder);
            _staged = Path.Combine(_layout.StagingFolder(To), "extracted");
            WriteApp(_folder, "old");
            WriteApp(_staged, "new");
        }
        else
        {
            _layout = new UpdateLayout(UpdatePlatform.MacOS, Path.Combine(_folder, "ShowTime.app"));
            _staged = Path.Combine(_layout.StagingFolder(To), "extracted", "ShowTime.app");
            WriteApp(_layout.AppPath, "old");
            WriteApp(_staged, "new");
        }
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_confirmed_update_replaces_the_app_and_deletes_the_old_one()
    {
        await RunAsync(ConfirmingNewVersion(), TimeSpan.FromSeconds(60));

        InstalledVersion().ShouldBe("new");
        Directory.Exists(_layout.BackupPath).ShouldBeFalse();
        Directory.Exists(_layout.StagingFolder(To)).ShouldBeFalse();
        File.Exists(_log).ShouldBeTrue();
        File.Exists(_oldStarted).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unconfirmed_update_restores_the_old_version()
    {
        await RunAsync(SilentNewVersion(), TimeSpan.FromSeconds(3));

        InstalledVersion().ShouldBe("old");
        Directory.Exists(_layout.StagingFolder(To)).ShouldBeFalse();
        Directory.Exists(_layout.BackupPath).ShouldBeFalse();
        // The script does not wait for the old version it starts again.
        (await EventuallyExistsAsync(_oldStarted)).ShouldBeTrue();
    }

    [Fact]
    public async Task User_files_in_the_install_folder_are_kept()
    {
        var notes = Path.Combine(_folder, "notes.txt");
        await File.WriteAllTextAsync(notes, "mes notes", TestContext.Current.CancellationToken);

        await RunAsync(ConfirmingNewVersion(), TimeSpan.FromSeconds(60));

        InstalledVersion().ShouldBe("new");
        (await File.ReadAllTextAsync(notes, TestContext.Current.CancellationToken)).ShouldBe("mes notes");
    }

    /// <summary>ShowTime.exe, LISEZ-MOI.txt and app\ on Windows; a bundle with its Contents on macOS.</summary>
    private void WriteApp(string root, string version)
    {
        if (_windows)
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, "ShowTime.exe"), version);
            File.WriteAllText(Path.Combine(root, "LISEZ-MOI.txt"), version);
            File.WriteAllText(Path.Combine(root, "app", "ffmpeg.exe"), version);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "ShowTime"), version);
        }
    }

    private string InstalledVersion() => _windows
        ? (File.ReadAllText(Path.Combine(_folder, "ShowTime.exe")) + "/" + File.ReadAllText(Path.Combine(_folder, "app", "ffmpeg.exe")))
            switch
        { "new/new" => "new", "old/old" => "old", var mixed => mixed }
        : File.ReadAllText(Path.Combine(_layout.AppPath, "Contents", "MacOS", "ShowTime"));

    private string ConfirmFile => _windows
        ? _layout.StagingFolder(To) + @"\confirmed"
        : _layout.StagingFolder(To) + "/confirmed";

    private IReadOnlyList<string> ConfirmingNewVersion() => WritingFile(ConfirmFile);

    private IReadOnlyList<string> SilentNewVersion() => _windows ? ["cmd.exe", "/c", "exit 0"] : ["/bin/sh", "-c", "exit 0"];

    /// <summary>A program that writes "ok" to <paramref name="path"/>, given as an argument so that it needs no quoting.</summary>
    private IReadOnlyList<string> WritingFile(string path)
    {
        if (!_windows)
            return ["/bin/sh", "-c", "echo ok > \"$1\"", "sh", path];
        var writer = Path.Combine(_temp.Path, "write.cmd");
        File.WriteAllText(writer, "@echo ok> \"%~1\"\r\n");
        return [writer, path];
    }

    private async Task RunAsync(IReadOnlyList<string> launchNew, TimeSpan confirmTimeout)
    {
        using var oldShowTime = _windows
            ? Process.Start(new ProcessStartInfo("ping", "-n 2 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!
            : Process.Start("sleep", "1");
        var plan = new ReplacementPlan(oldShowTime.Id, _layout, _staged, From, To, ConfirmFile, _log, confirmTimeout,
            launchNew, WritingFile(_oldStarted));
        var text = _windows ? ReplacementScript.ForWindows(plan) : ReplacementScript.ForMacOS(plan);
        var script = ScriptLauncher.Write(_layout.Platform, Path.Combine(_temp.Path, "work"), text);

        using var run = ScriptLauncher.StartDetached(_layout.Platform, script);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScriptTimeout);
        await run.WaitForExitAsync(timeout.Token);
    }

    private static async Task<bool> EventuallyExistsAsync(string path)
    {
        for (var i = 0; i < 100 && !File.Exists(path); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        return File.Exists(path);
    }
}
