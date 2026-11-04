using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Diagnostics;
using ShowTime.Core.Transcription;

namespace ShowTime.Mac;

/// <summary>Starts ShowTime as an icon in the menu bar (<see cref="StatusMenu"/>), and owns the services for the app's life.</summary>
[Register(nameof(AppDelegate))]
internal sealed class AppDelegate : NSApplicationDelegate
{
    private ServiceProvider? _services;

    private static string Version =>
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "";

    public override void DidFinishLaunching(NSNotification notification)
    {
        _services = Composition.Build();
        var log = _services.GetRequiredService<ILogger<AppDelegate>>();
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("ShowTime {Version} started", Version);

        // The microphone tracks of a previous run, closed before they were transcribed.
        _services.GetRequiredService<MicrophoneTracks>().DeleteLeftovers();
        _services.GetRequiredService<StatusMenu>();
        _services.GetRequiredService<HangWatchdog>().Start();

        // "open -a ShowTime --args --test-hang": the main thread freezes for a minute, to check the watchdog by hand.
        if (NSProcessInfo.ProcessInfo.Arguments.Contains("--test-hang"))
            DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromSeconds(5)),
                () => Thread.Sleep(TimeSpan.FromMinutes(1)));
    }

    /// <summary>Quitting stops a recording and asks about every capture not saved yet; "Annuler" keeps ShowTime running.</summary>
    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender) =>
        _services?.GetService<StatusMenu>()?.ShouldTerminate() ?? NSApplicationTerminateReply.Now;

    public override void WillTerminate(NSNotification notification)
    {
        _services?.GetService<ILogger<AppDelegate>>()?.LogInformation("ShowTime quits");
        _services?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _services?.Dispose();
        base.Dispose(disposing);
    }
}
