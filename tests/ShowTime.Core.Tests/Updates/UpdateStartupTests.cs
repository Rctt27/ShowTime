using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class UpdateStartupTests
{
    private static readonly SemanticVersion Current = new(0, 6, 3);

    private readonly MockFileSystem _fs = new();
    private readonly IUserDialogs _dialogs = Substitute.For<IUserDialogs>();
    private readonly ListLogger<UpdateStartupTests> _log = new();
    private readonly UpdateLayout _layout;
    private readonly string _staging;

    public UpdateStartupTests()
    {
        // The host's own paths, so that the mock file system enumerates them on every OS.
        var install = Path.Combine(Path.GetTempPath(), "ShowTimeInstall");
        _layout = new UpdateLayout(OperatingSystem.IsWindows() ? UpdatePlatform.WindowsX64 : UpdatePlatform.MacOS,
            OperatingSystem.IsWindows() ? install : Path.Combine(install, "ShowTime.app"));
        _staging = _layout.StagingFolder(new SemanticVersion(0, 7, 0));
        _fs.AddDirectory(Path.Combine(_staging, "extracted"));
        _fs.AddDirectory(Path.Combine(_layout.BackupPath, "app"));
        _fs.AddDirectory(_layout.AppPath);
    }

    private void Run(params string[] args) =>
        UpdateStartup.Run(UpdateArguments.Parse(args), _layout, _fs, _dialogs, Current, _log);

    [Fact]
    public void Parse_reads_the_confirmation_file()
    {
        UpdateArguments.Parse(["--update-confirm", "/tmp/confirmed"])
            .ShouldBe(new UpdateArguments("/tmp/confirmed", null));
    }

    [Fact]
    public void Parse_reads_the_version_that_failed()
    {
        UpdateArguments.Parse(["--update-failed", "0.7.0"])
            .ShouldBe(new UpdateArguments(null, new SemanticVersion(0, 7, 0)));
    }

    [Fact]
    public void Parse_ignores_unknown_arguments_and_a_malformed_version()
    {
        UpdateArguments.Parse(["--diagnostics", "--update-failed", "0.7.0-beta", "--test-hang"])
            .ShouldBe(new UpdateArguments(null, null));
        UpdateArguments.Parse(["--update-confirm"]).ShouldBe(new UpdateArguments(null, null));
        UpdateArguments.Parse([]).ShouldBe(new UpdateArguments(null, null));
    }

    [Fact]
    public void Leftovers_are_kept_while_an_update_is_being_confirmed()
    {
        var confirm = Path.Combine(_staging, "confirmed");

        Run("--update-confirm", confirm);

        _fs.File.Exists(confirm).ShouldBeTrue();
        _fs.Directory.Exists(_staging).ShouldBeTrue();
        _fs.Directory.Exists(_layout.BackupPath).ShouldBeTrue();
        _dialogs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void A_confirmation_that_cannot_be_written_is_logged()
    {
        // The script then restores the old version: starting must go on meanwhile.
        var confirm = Path.Combine(_staging, "missing", "confirmed");

        Run("--update-confirm", confirm);

        _fs.File.Exists(confirm).ShouldBeFalse();
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void A_failed_update_is_reported_with_the_current_version_and_its_leftovers_deleted()
    {
        Run("--update-failed", "0.7.0");

        _dialogs.Received(1).ShowWarning("La version 0.7.0 n'a pas pu démarrer ; ShowTime est resté en version 0.6.3.");
        _fs.Directory.Exists(_staging).ShouldBeFalse();
        _fs.Directory.Exists(_layout.BackupPath).ShouldBeFalse();
    }

    [Fact]
    public void A_normal_start_deletes_the_leftovers_and_says_nothing()
    {
        Run();

        _fs.Directory.Exists(_staging).ShouldBeFalse();
        _fs.Directory.Exists(_layout.BackupPath).ShouldBeFalse();
        _fs.Directory.Exists(_layout.AppPath).ShouldBeTrue();
        _dialogs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void A_leftover_that_cannot_be_deleted_is_logged_and_skipped()
    {
        // A locked folder (a file still open in it) cannot be deleted: starting goes on, the next start tries again.
        var fs = Substitute.For<IFileSystem>();
        fs.Path.Returns(_fs.Path);
        fs.Directory.Exists(Arg.Any<string>()).Returns(true);
        fs.Directory.EnumerateDirectories(Arg.Any<string>()).Returns([_layout.BackupPath, _staging]);
        fs.Directory.When(d => d.Delete(_layout.BackupPath, true)).Do(_ => throw new IOException("locked"));

        UpdateStartup.Run(UpdateArguments.Parse([]), _layout, fs, _dialogs, Current, _log);

        fs.Directory.Received(1).Delete(_staging, true);
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is IOException);
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_is_logged()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.Directory.Exists(Arg.Any<string>()).Returns(true);
        fs.Directory.EnumerateDirectories(Arg.Any<string>()).Returns(_ => throw new UnauthorizedAccessException());

        UpdateStartup.Run(UpdateArguments.Parse(["--update-failed", "0.7.0"]), _layout, fs, _dialogs, Current, _log);

        _log.Entries.ShouldContain(e => e.Exception is UnauthorizedAccessException);
        _dialogs.Received(1).ShowWarning(Arg.Any<string>());
    }
}
