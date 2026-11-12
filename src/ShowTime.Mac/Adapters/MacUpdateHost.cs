using AppKit;
using Foundation;
using ShowTime.Core.Ports;
using ShowTime.Mac.Editor;

namespace ShowTime.Mac.Adapters;

/// <summary>What an update needs from the macOS app: closing the editors, quitting, opening the release page.</summary>
internal sealed class MacUpdateHost(MacEditorLauncher editor) : IUpdateHost
{
    /// <summary>Set before quitting for an update: <see cref="StatusMenu.ShouldTerminate"/> then asks nothing again.</summary>
    public static bool QuitApproved { get; private set; }

    /// <summary>Every editor with an unsaved capture asks first; keeping one open stops here.</summary>
    public bool TryCloseWindows() => editor.CloseAll();

    public void Quit()
    {
        QuitApproved = true;
        NSApplication.SharedApplication.Terminate(NSApplication.SharedApplication);
    }

    public void OpenInBrowser(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url.AbsoluteUri));
    }
}
