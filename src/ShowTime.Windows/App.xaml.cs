using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowTime.Adapters;
using ShowTime.Core.Capture;
using ShowTime.Core.Files;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Infrastructure.Diagnostics;
using ShowTime.Infrastructure.Logging;
using ShowTime.Infrastructure.Platform;
using ShowTime.Infrastructure.Recording;
using ShowTime.Infrastructure.Settings;
using ShowTime.Infrastructure.Transcription;
using ShowTime.Platform.Windows;

namespace ShowTime;

// The mutex and the container are released in OnExit, the end of a WPF application's life.
#pragma warning disable CA1001
public partial class App : Application
#pragma warning restore CA1001
{
    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before the single-instance check: the report is also useful while ShowTime runs (its shortcuts then show as taken).
        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(await RunDiagnosticsAsync());
            return;
        }

        _singleInstance = new Mutex(true, @"Local\ShowTime.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("ShowTime est déjà lancé (icône dans la zone de notification).", "ShowTime",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        _services = ConfigureServices(Dispatcher).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var log = _services.GetRequiredService<ILogger<App>>();
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("ShowTime {Version} started", typeof(App).Assembly.GetName().Version);

        // One failed action must not take down the tray process and every open editor with it.
        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled exception");
            MessageBox.Show("Une erreur inattendue s'est produite : " + args.Exception.Message, "ShowTime",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Tracks of videos closed before their transcription: their players are gone with the previous run.
        _services.GetRequiredService<MicrophoneTracks>().DeleteLeftovers();
        _services.GetRequiredService<TrayController>();
    }

    /// <summary>"ShowTime.exe --diagnostics": the report, in the terminal it was started from and in the log folder.</summary>
    private static async Task<int> RunDiagnosticsAsync()
    {
        using var services = ConfigureServices(Current.Dispatcher).BuildServiceProvider();
        var reportFile = Path.Combine(services.GetRequiredService<IAppPaths>().LogsFolder, "diagnostics.txt");
        using var output = DiagnosticsConsole.Open(reportFile);
        var code = await services.GetRequiredService<DiagnosticsReport>().RunAsync(output);
        output.WriteLine("Report saved to " + reportFile);
        return code;
    }

    /// <summary>The composition root: the core's use cases with the Windows adapters.</summary>
    internal static ServiceCollection ConfigureServices(System.Windows.Threading.Dispatcher dispatcher)
    {
        var paths = AppPaths.ForCurrentPlatform();
        var defaults = SettingsDefaults.ForWindows(paths.DefaultOutputFolder);
        var services = new ServiceCollection();

        services.AddLogging(logging => logging.AddProvider(new FileLoggerProvider(paths.LogsFolder, TimeProvider.System)));
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(defaults);
        services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(sp.GetRequiredService<IFileSystem>(), paths.SettingsFile, defaults));
        services.AddSingleton(sp => sp.GetRequiredService<ISettingsStore>().Load());

        // Core
        services.AddSingleton<OutputPathAllocator>();
        services.AddSingleton<FileCounter>();
        services.AddSingleton<ImageSaver>();
        services.AddSingleton<CaptureDelivery>();
        services.AddSingleton<CaptureWorkflow>();
        services.AddSingleton(sp => AppPaths.CreateFfmpegLocator(sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<ISystemAudioSource, WasapiSystemAudio>();
        services.AddSingleton<IFfmpegPlatform>(sp => new DirectShowGdigrab(sp.GetRequiredService<ISystemAudioSource>().IsAvailable));
        // Start and Stop are registered apart from the capture shortcuts, only while a recording is being prepared or made.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<RecordingSession>(sp,
            (IGlobalHotkeys)new WindowsHotkeys(sp.GetRequiredService<IUiThread>()), HotkeyNaming.Windows));
        services.AddSingleton<IRecordingStatus>(sp => sp.GetRequiredService<RecordingSession>());
        // The highlight trigger is registered only while recording, like Start and Stop.
        services.AddSingleton<IHighlightInput>(sp => new WindowsHighlightInput(sp.GetRequiredService<IUiThread>()));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Highlighter>(sp, HotkeyNaming.Windows));
        services.AddSingleton(sp => paths.CreateModelLocator(sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<MicrophoneTracks>();
        services.AddSingleton<SubtitleFile>();
        services.AddSingleton<Transcriptions>();

        // Infrastructure
        services.AddSingleton<IAudioDeviceLister, FfmpegAudioDeviceLister>();
        services.AddSingleton<IVideoRecorderFactory, FfmpegVideoRecorderFactory>();
        // The zip keeps runtimes\win-x64 in its app folder; a development build has it next to the exe.
        WhisperTranscriber.LoadNativeLibrariesFrom(AppPaths.WindowsShippedFolder(AppContext.BaseDirectory));
        services.AddSingleton<ITranscriber, WhisperTranscriber>();
        services.AddSingleton<IAudioExtractor, FfmpegAudioExtractor>();
        services.AddSingleton<DiagnosticsReport>();

        // Windows adapters
        services.AddSingleton<IScreenGrabber, WindowsScreenGrabber>();
        services.AddSingleton<INativeWindows, WindowsNativeWindows>();
        services.AddSingleton<IShell, WindowsShell>();
        services.AddSingleton<IFileTrash, WindowsFileTrash>();
        services.AddSingleton<IStartupRegistration>(
            new WindowsStartupRegistration(WindowsStartupRegistration.RunKeyPath, "ShowTime", Environment.ProcessPath!));
        services.AddSingleton<WpfScreen>();
        services.AddSingleton<IScreen>(sp => sp.GetRequiredService<WpfScreen>());
        services.AddSingleton<IAreaPicker>(sp => sp.GetRequiredService<WpfScreen>());
        services.AddSingleton<IUiThread>(new DispatcherUiThread(dispatcher));
        services.AddSingleton<IUserDialogs, WpfDialogs>();
        services.AddSingleton<IClipboard, WpfClipboard>();
        services.AddSingleton<IImageCodec, WpfImageCodec>();
        services.AddSingleton<IRecordingViewFactory, WpfRecordingViewFactory>();
        services.AddSingleton<IHighlightOverlayFactory, HighlightOverlayFactory>();
        services.AddSingleton<IGlobalHotkeys>(sp => new WindowsHotkeys(sp.GetRequiredService<IUiThread>()));
        services.AddSingleton<TrayIcon>();
        services.AddSingleton<INotifications>(sp => sp.GetRequiredService<TrayIcon>());
        services.AddSingleton<EditorContext>();
        services.AddSingleton<IEditorLauncher, WpfEditorLauncher>();
        services.AddSingleton(sp => new SettingsEditor(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IFileSystem>(), @"C:\Users\Moi\Captures"));
        services.AddSingleton<SettingsContext>();
        services.AddSingleton<Func<SettingsWindow>>(sp => () => new SettingsWindow(sp.GetRequiredService<SettingsContext>()));
        services.AddSingleton<TrayController>();
        return services;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
