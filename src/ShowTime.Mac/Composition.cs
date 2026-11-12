using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Capture;
using ShowTime.Core.Diagnostics;
using ShowTime.Core.Files;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Core.Updates;
using ShowTime.Infrastructure.Diagnostics;
using ShowTime.Infrastructure.Logging;
using ShowTime.Infrastructure.Platform;
using ShowTime.Infrastructure.Recording;
using ShowTime.Infrastructure.Settings;
using ShowTime.Infrastructure.Transcription;
using ShowTime.Infrastructure.Updates;
using ShowTime.Mac.Adapters;
using ShowTime.Mac.Capture;
using ShowTime.Mac.Editor;
using ShowTime.Mac.Platform;
using ShowTime.Mac.Recording;
using ShowTime.Mac.Settings;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac;

/// <summary>The composition root: the core's use cases with the macOS adapters.</summary>
internal static class Composition
{
    public static ServiceProvider Build()
    {
        var paths = AppPaths.ForCurrentPlatform();
        var defaults = SettingsDefaults.ForMacOS(paths.DefaultOutputFolder);
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
        // The screens recorded by ffmpeg are wired in with the recording.
        // ffmpeg numbers the screens in NSScreen's order.
        services.AddSingleton<IFfmpegPlatform>(sp => new AvFoundation(sp.GetRequiredService<IDisplays>().Screens));
        // Start and Stop are registered apart from the capture shortcuts, only while a recording is being prepared or made.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<RecordingSession>(sp,
            (IGlobalHotkeys)new MacHotkeys(sp.GetRequiredService<IUiThread>()), HotkeyNaming.MacOS));
        services.AddSingleton<IRecordingStatus>(sp => sp.GetRequiredService<RecordingSession>());
        // The highlight trigger is registered only while recording, like Start and Stop.
        services.AddSingleton<IHighlightInput>(sp => new MacHighlightInput(sp.GetRequiredService<IUiThread>()));
        services.AddSingleton<IHighlightOverlayFactory, MacHighlightOverlayFactory>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Highlighter>(sp, HotkeyNaming.MacOS));
        services.AddSingleton(sp => paths.CreateModelLocator(sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<MicrophoneTracks>();
        services.AddSingleton<SubtitleFile>();
        services.AddSingleton<Transcriptions>();

        // Infrastructure
        services.AddSingleton<IAudioDeviceLister, FfmpegAudioDeviceLister>();
        services.AddSingleton<IVideoRecorderFactory, FfmpegVideoRecorderFactory>();
        // The Mac's sound and the microphone, captured by ShowTime itself and piped to ffmpeg.
        services.AddSingleton<ISystemAudioSource, MacSystemAudio>();
        services.AddSingleton<IMicrophoneSource, MacMicrophone>();
        services.AddSingleton<ITranscriber, WhisperTranscriber>();
        services.AddSingleton<IAudioExtractor, FfmpegAudioExtractor>();
        services.AddSingleton<DiagnosticsReport>();
        // A frozen main thread is noticed, recorded, and the user offered to force ShowTime to quit.
        services.AddSingleton<IHangResponder, MacHangResponder>();
        services.AddSingleton(sp => new HangWatchdog(sp.GetRequiredService<TimeProvider>(), new RunLoopMainThread(),
            sp.GetRequiredService<IHangResponder>()));

        // macOS adapters
        services.AddSingleton<IUiThread>(new MainThread());
        services.AddSingleton<IDisplays, MacDisplays>();
        services.AddSingleton<IRecordingViewFactory, MacRecordingViewFactory>();
        services.AddSingleton<IScreenGrabber, MacScreenGrabber>();
        services.AddSingleton<INativeWindows, MacNativeWindows>();
        services.AddSingleton<IClipboard, MacClipboard>();
        services.AddSingleton<IImageCodec, MacImageCodec>();
        services.AddSingleton<IShell, MacShell>();
        services.AddSingleton<IFileTrash, MacFileTrash>();
        services.AddSingleton<IStartupRegistration, MacStartupRegistration>();
        services.AddSingleton<IGlobalHotkeys>(sp => new MacHotkeys(sp.GetRequiredService<IUiThread>()));
        services.AddSingleton<MacScreen>();
        services.AddSingleton<IScreen>(sp => sp.GetRequiredService<MacScreen>());
        services.AddSingleton<IAreaPicker>(sp => sp.GetRequiredService<MacScreen>());
        services.AddSingleton<IUserDialogs, MacDialogs>();
        services.AddSingleton<INotifications, MacNotifications>();
        services.AddSingleton(sp => new EditorContext(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ImageSaver>(), sp.GetRequiredService<IClipboard>(), sp.GetRequiredService<IShell>()));
        services.AddSingleton<MacEditorLauncher>();
        services.AddSingleton<IEditorLauncher>(sp => sp.GetRequiredService<MacEditorLauncher>());
        services.AddSingleton(sp => new SettingsEditor(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IFileSystem>(), "/Users/moi/Captures"));
        services.AddSingleton<SettingsContext>();
        services.AddSingleton<Func<SettingsWindowController>>(sp => () => new SettingsWindowController(sp.GetRequiredService<SettingsContext>()));
        services.AddSingleton<StatusMenu>();

        // Updates: GitHub's latest release, installed over ShowTime.app where it is.
        var current = SemanticVersion.From(typeof(Composition).Assembly.GetName().Version!);
        services.AddSingleton<IReleaseFeed>(new GitHubReleaseFeed(new SocketsHttpHandler(), current));
        services.AddSingleton<IUpdateDownloader>(new HttpUpdateDownloader(new SocketsHttpHandler()));
        services.AddSingleton<IUpdateInstaller, MacUpdateInstaller>();
        services.AddSingleton<IUpdateHost, MacUpdateHost>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Updater>(sp, current));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
