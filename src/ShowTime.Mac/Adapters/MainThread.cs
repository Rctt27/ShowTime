using AppKit;
using CoreFoundation;
using ShowTime.Core.Ports;

namespace ShowTime.Mac.Adapters;

/// <summary>AppKit's main thread, through the main dispatch queue, which the application's run loop drains.</summary>
internal sealed class MainThread : IUiThread
{
    public void Post(Action action) => DispatchQueue.MainQueue.DispatchAsync(action);
}

/// <summary>
/// AppKit's main thread through its run loop rather than the main dispatch queue: the hang watchdog's ping. The queue
/// is serial: an alert run from one of its blocks (quitting during a recording, a global shortcut) would hold back a
/// ping sent through it, and pass for a hang. The run loop's own sources are served in the alert's modal loop too.
/// </summary>
internal sealed class RunLoopMainThread : IUiThread
{
    public void Post(Action action) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
}
