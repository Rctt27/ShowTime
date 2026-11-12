using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using ShowTime.Core.Ports;
using ShowTime.Core.Updates;
using ShowTime.Infrastructure.Diagnostics;

namespace ShowTime.Mac;

internal static class Program
{
    private static void Main(string[] args)
    {
        NSApplication.Init();
        var app = NSApplication.SharedApplication;

        // "ShowTime.app/Contents/MacOS/ShowTime --diagnostics", from a terminal. It runs inside the application's run
        // loop, as the app does, since the shortcut check needs the main thread.
        if (args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            app.ActivationPolicy = NSApplicationActivationPolicy.Prohibited;
            app.BeginInvokeOnMainThread(async () => Environment.Exit(await RunDiagnosticsAsync().ConfigureAwait(true)));
            app.Run();
            return;
        }

        // A second launch, from the Finder or at login, leaves the running one alone instead of adding an icon.
        // Compared by process, since an app started from a terminal is not listed until it has finished launching.
        // Started by the update script, the old version may still be listed for a moment as it exits.
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        var patience = UpdateArguments.Parse(args).ConfirmFile is not null ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        if (bundleId is not null && AnotherIsRunning(bundleId, patience))
            return;

        app.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
        app.MainMenu = MainMenu.Create();
        app.Delegate = new AppDelegate();
        app.Run();
    }

    private static bool AnotherIsRunning(string bundleId, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        while (true)
        {
            if (!NSRunningApplication.GetRunningApplications(bundleId).Any(other => other.ProcessIdentifier != Environment.ProcessId))
                return false;
            if (DateTime.UtcNow >= deadline)
                return true;
            Thread.Sleep(200);
        }
    }

    private static async Task<int> RunDiagnosticsAsync()
    {
        try
        {
            using var services = Composition.Build();
            var reportFile = Path.Combine(services.GetRequiredService<IAppPaths>().LogsFolder, "diagnostics.txt");
            using var output = DiagnosticsOutput.Open(reportFile, Console.Out);
            var code = await services.GetRequiredService<DiagnosticsReport>().RunAsync(output).ConfigureAwait(true);
            output.WriteLine("Report saved to " + reportFile);
            return code;
        }
#pragma warning disable CA1031 // Whatever stops the report is the most useful thing it can print.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await Console.Error.WriteLineAsync("Diagnostics failed: " + ex).ConfigureAwait(true);
            return 1;
        }
    }
}
