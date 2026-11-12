using Microsoft.Extensions.DependencyInjection;
using ShowTime.Core.Recording;

namespace ShowTime.Windows.Tests;

/// <summary>ValidateOnBuild does not look inside factories: the recording session is resolved for real.</summary>
public sealed class CompositionTests
{
    [Fact]
    public async Task The_recording_session_and_its_highlighter_can_be_built() => await Sta.Run(() =>
    {
        using var services = App.ConfigureServices(System.Windows.Threading.Dispatcher.CurrentDispatcher).BuildServiceProvider();
        services.GetRequiredService<RecordingSession>().ShouldNotBeNull();
        services.GetRequiredService<Highlighter>().ShouldNotBeNull();
    });

    [Fact]
    public async Task The_updater_can_be_built_and_replaces_the_exes_folder() => await Sta.Run(() =>
    {
        using var services = App.ConfigureServices(System.Windows.Threading.Dispatcher.CurrentDispatcher).BuildServiceProvider();
        services.GetRequiredService<ShowTime.Core.Updates.Updater>().ShouldNotBeNull();
        services.GetRequiredService<ShowTime.Core.Ports.IUpdateInstaller>().Layout.AppPath
            .ShouldBe(System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    });
}
