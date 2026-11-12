using System.IO.Abstractions;
using System.IO.Compression;
using NSubstitute;
using ShowTime.Core.Ports;
using ShowTime.Infrastructure.Updates;

namespace ShowTime.Infrastructure.IntegrationTests.Updates;

public sealed class WindowsUpdateInstallerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly IAppPaths _paths = Substitute.For<IAppPaths>();
    private readonly string _app;
    private readonly WindowsUpdateInstaller _installer;

    public WindowsUpdateInstallerTests()
    {
        _app = Path.Combine(_temp.Path, "ShowTime");
        Directory.CreateDirectory(_app);
        _paths.WorkFolder.Returns(Path.Combine(_temp.Path, "work"));
        _installer = new WindowsUpdateInstaller(_paths, new FileSystem(), _app);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Staging_installs_the_content_of_the_zips_ShowTime_folder()
    {
        // release.yml zips build/ShowTime: everything sits under a top-level ShowTime folder.
        var zip = Zip(("ShowTime/ShowTime.exe", "exe"), ("ShowTime/app/ffmpeg.exe", "ffmpeg"));
        var staging = Path.Combine(_app, ".update-0.7.0");

        var staged = await _installer.StageAsync(zip, staging, TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(staged, "ShowTime.exe")).ShouldBe("exe");
        File.ReadAllText(Path.Combine(staged, "app", "ffmpeg.exe")).ShouldBe("ffmpeg");
        staged.ShouldStartWith(Path.Combine(staging, "extracted"));
    }

    [Fact]
    public async Task A_zip_without_ShowTime_exe_is_refused()
    {
        var zip = Zip(("ShowTime/LISEZ-MOI.txt", "texte"));

        var failure = await Should.ThrowAsync<UpdateException>(
            () => _installer.StageAsync(zip, Path.Combine(_app, ".update-0.7.0"), TestContext.Current.CancellationToken));

        failure.Message.ShouldBe("l'archive ne contient pas ShowTime.exe");
    }

    [Fact]
    public async Task An_unreadable_zip_is_refused()
    {
        var zip = Path.Combine(_temp.Path, "broken.zip");
        await File.WriteAllTextAsync(zip, "not a zip", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<UpdateException>(
            () => _installer.StageAsync(zip, Path.Combine(_app, ".update-0.7.0"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_development_build_cannot_be_replaced()
    {
        // No app\ffmpeg.exe beside the exe: a build from the repository, not an install from the zip.
        _installer.CanReplaceApp().ShouldBeFalse();
    }

    [Fact]
    public void An_installed_zip_in_a_writable_folder_can_be_replaced()
    {
        Directory.CreateDirectory(Path.Combine(_app, "app"));
        File.WriteAllText(Path.Combine(_app, "app", "ffmpeg.exe"), "ffmpeg");

        _installer.CanReplaceApp().ShouldBeTrue();
        Directory.GetFiles(_app).ShouldBeEmpty();
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }
}
