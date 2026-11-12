using System.IO;
using System.IO.Abstractions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NSubstitute;
using ShowTime.Adapters;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;

namespace ShowTime.Windows.Tests.Settings;

public sealed class SettingsWindowTests
{
    private static readonly string Out = Path.GetTempPath().TrimEnd('\\');
    private readonly FileSystem _fs = new();
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();

    public SettingsWindowTests()
    {
        var defaults = SettingsDefaults.ForWindows(Out);
        _settings = new AppSettings(defaults);
    }

    private SettingsWindow Create()
    {
        var defaults = SettingsDefaults.ForWindows(Out);
        var context = new SettingsContext(
            _settings, _store, defaults, new FfmpegLocator(_fs, @"C:\App", null, "ffmpeg.exe", []),
            Substitute.For<IFfmpegPlatform>(), Substitute.For<IAudioDeviceLister>(), Substitute.For<IStartupRegistration>(),
            new SettingsEditor(_settings, _store, _fs, @"C:\Users\Moi\Captures"), new TranscriptionModelLocator(_fs, []),
            Substitute.For<IHighlightOverlayFactory>(), new DispatcherUiThread(Dispatcher.CurrentDispatcher), TimeProvider.System,
            Substitute.For<IRecordingStatus>());
        return new SettingsWindow(context);
    }

    [Fact]
    public async Task The_update_check_box_shows_the_setting_and_saves_it() => await Sta.Run(() =>
    {
        var window = Create();
        var box = (CheckBox)window.FindName("CheckForUpdatesBox");
        box.Content.ShouldBe("Rechercher automatiquement les mises à jour");
        box.IsChecked.ShouldBe(true);

        box.IsChecked = false;
        var save = window.FindName("SaveButton").ShouldBeOfType<Button>();
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        _settings.CheckForUpdates.ShouldBeFalse();
        _store.Received().Save(_settings);
        window.Close();
    });
}
