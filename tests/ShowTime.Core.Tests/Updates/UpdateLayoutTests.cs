using System.IO.Abstractions.TestingHelpers;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class UpdateLayoutTests
{
    private static readonly SemanticVersion Next = new(0, 7, 0);

    [Fact]
    public void Windows_stages_and_backs_up_inside_the_install_folder()
    {
        var layout = new UpdateLayout(UpdatePlatform.WindowsX64, @"C:\Tools\ShowTime");

        layout.StagingFolder(Next).ShouldBe(@"C:\Tools\ShowTime\.update-0.7.0");
        layout.BackupPath.ShouldBe(@"C:\Tools\ShowTime\.old");
        layout.Items.ShouldBe(["ShowTime.exe", "LISEZ-MOI.txt", "app"]);
    }

    [Fact]
    public void Windows_ignores_the_trailing_separator_of_the_base_directory()
    {
        // AppContext.BaseDirectory ends with one.
        var layout = new UpdateLayout(UpdatePlatform.WindowsX64, @"C:\Tools\ShowTime\");

        layout.AppPath.ShouldBe(@"C:\Tools\ShowTime");
        layout.BackupPath.ShouldBe(@"C:\Tools\ShowTime\.old");
    }

    [Fact]
    public void MacOS_stages_and_backs_up_beside_the_bundle()
    {
        var layout = new UpdateLayout(UpdatePlatform.MacOS, "/Applications/ShowTime.app");

        layout.StagingFolder(Next).ShouldBe("/Applications/.ShowTime-update-0.7.0");
        layout.BackupPath.ShouldBe("/Applications/.ShowTime-old.app");
        layout.Items.ShouldBe(["ShowTime.app"]);
    }

    [Fact]
    public void MacOS_keeps_the_bundle_name_it_was_given()
    {
        var layout = new UpdateLayout(UpdatePlatform.MacOS, "/Users/zoé/Applications/Show Time.app/");

        layout.AppPath.ShouldBe("/Users/zoé/Applications/Show Time.app");
        layout.Items.ShouldBe(["Show Time.app"]);
        layout.BackupPath.ShouldBe("/Users/zoé/Applications/.ShowTime-old.app");
    }

    [Fact]
    public void Windows_leftovers_are_the_staging_folders_and_the_backup()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths need a Windows file system.");
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\Tools\ShowTime\.update-0.6.9\extracted");
        fs.AddDirectory(@"C:\Tools\ShowTime\.old\app");
        fs.AddDirectory(@"C:\Tools\ShowTime\app");
        fs.AddDirectory(@"C:\Tools\ShowTime\.update-notes");
        fs.AddDirectory(@"C:\Tools\ShowTime\.older");
        fs.AddFile(@"C:\Tools\ShowTime\.update-0.7.0", new MockFileData(""));

        var layout = new UpdateLayout(UpdatePlatform.WindowsX64, @"C:\Tools\ShowTime");

        layout.Leftovers(fs).ShouldBe([@"C:\Tools\ShowTime\.update-0.6.9", @"C:\Tools\ShowTime\.old"], ignoreOrder: true);
    }

    [Fact]
    public void MacOS_leftovers_are_the_staging_folders_and_the_backup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths need a Unix file system.");
        var fs = new MockFileSystem();
        fs.AddDirectory("/Applications/.ShowTime-update-0.6.9/extracted/ShowTime.app");
        fs.AddDirectory("/Applications/.ShowTime-old.app/Contents");
        fs.AddDirectory("/Applications/ShowTime.app/Contents");
        fs.AddDirectory("/Applications/.update-0.6.9");
        fs.AddDirectory("/Applications/Safari.app");

        var layout = new UpdateLayout(UpdatePlatform.MacOS, "/Applications/ShowTime.app");

        layout.Leftovers(fs).ShouldBe(
            ["/Applications/.ShowTime-update-0.6.9", "/Applications/.ShowTime-old.app"], ignoreOrder: true);
    }

    [Fact]
    public void No_leftovers_when_the_folder_is_gone()
    {
        var layout = new UpdateLayout(UpdatePlatform.MacOS, "/Volumes/Gone/ShowTime.app");

        layout.Leftovers(new MockFileSystem()).ShouldBeEmpty();
    }
}
