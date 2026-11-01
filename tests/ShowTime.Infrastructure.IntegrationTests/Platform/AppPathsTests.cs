using System.IO.Abstractions;
using NSubstitute;
using ShowTime.Infrastructure.Platform;

namespace ShowTime.Infrastructure.IntegrationTests.Platform;

public sealed class AppPathsTests
{
    [Fact]
    public void Windows_output_folder_is_in_Pictures_when_it_is_not_synced() =>
        AppPaths.WindowsOutputFolder(@"C:\Users\Me\Pictures", @"C:\Users\Me", [@"C:\Users\Me\OneDrive"])
            .ShouldBe(Path.Combine(@"C:\Users\Me\Pictures", "ShowTime"));

    [Fact]
    public void Windows_output_folder_avoids_a_Pictures_folder_synced_by_OneDrive() =>
        AppPaths.WindowsOutputFolder(@"C:\Users\Me\OneDrive\Images", @"C:\Users\Me", [@"c:\users\me\onedrive"])
            .ShouldBe(Path.Combine(@"C:\Users\Me", "Captures ShowTime"));

    [Fact]
    public void Windows_output_folder_avoids_an_unknown_Pictures_folder() =>
        AppPaths.WindowsOutputFolder("", @"C:\Users\Me", []).ShouldBe(Path.Combine(@"C:\Users\Me", "Captures ShowTime"));

    [Theory]
    [InlineData("/Applications/ShowTime.app/Contents/MonoBundle/", "/Applications/ShowTime.app/Contents/MacOS")]
    [InlineData("/Users/moi/Downloads/ShowTime 2.app/Contents/MonoBundle", "/Users/moi/Downloads/ShowTime 2.app/Contents/MacOS")]
    [InlineData("/private/var/folders/ab/T/AppTranslocation/1234/d/ShowTime.app/Contents/MonoBundle/",
        "/private/var/folders/ab/T/AppTranslocation/1234/d/ShowTime.app/Contents/MacOS")]
    public void The_bundled_ffmpeg_is_found_wherever_the_app_is(string assemblies, string expected) =>
        AppPaths.FfmpegDirectory(assemblies).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\Program Files\ShowTime\")]
    [InlineData("/Users/moi/ShowTime/src/ShowTime.Mac/bin/Debug/net10.0-macos/osx-arm64/")]
    public void Outside_a_bundle_the_assemblies_folder_is_searched(string assemblies) =>
        AppPaths.FfmpegDirectory(assemblies).ShouldBe(assemblies);

    [Fact]
    public void The_current_platform_has_absolute_paths()
    {
        var paths = AppPaths.ForCurrentPlatform();
        Path.IsPathFullyQualified(paths.SettingsFile).ShouldBeTrue();
        Path.IsPathFullyQualified(paths.LogsFolder).ShouldBeTrue();
        Path.IsPathFullyQualified(paths.DefaultOutputFolder).ShouldBeTrue();
        Path.IsPathFullyQualified(paths.WorkFolder).ShouldBeTrue();
        Path.GetFileName(paths.SettingsFile).ShouldBe("settings.json");
    }

    [Fact]
    public void The_Windows_zip_keeps_what_ShowTime_needs_in_an_app_folder() =>
        AppPaths.WindowsShippedFolder(@"C:\Users\Me\ShowTime\").ShouldBe(Path.Combine(@"C:\Users\Me\ShowTime\", "app"));

    [Fact]
    public void The_model_shipped_with_ShowTime_is_looked_for_first()
    {
        var shipped = OperatingSystem.IsWindows() ? AppPaths.WindowsShippedFolder(AppContext.BaseDirectory) : AppContext.BaseDirectory;
        AppPaths.ForCurrentPlatform().ModelsFolders[0].ShouldBe(Path.Combine(shipped, "models"));
    }

    [Fact]
    public void On_Windows_the_shipped_ffmpeg_is_the_one_in_the_app_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var shipped = Path.Combine(AppPaths.WindowsShippedFolder(AppContext.BaseDirectory), "ffmpeg.exe");
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Combine(Arg.Any<string>(), Arg.Any<string>()).Returns(call => Path.Combine(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        fileSystem.File.Exists(Arg.Any<string>()).Returns(call => call.ArgAt<string>(0) == shipped);

        AppPaths.CreateFfmpegLocator(fileSystem).Find("").ShouldBe(shipped);
    }

    [Fact]
    public void On_macOS_settings_go_to_Application_Support()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        AppPaths.ForCurrentPlatform().SettingsFile.ShouldContain("Library/Application Support/ShowTime");
    }
}
