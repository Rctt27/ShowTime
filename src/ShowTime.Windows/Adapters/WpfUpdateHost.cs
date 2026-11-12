using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using ShowTime.Core.Ports;

namespace ShowTime.Adapters;

/// <summary>What an update needs from the WPF app: closing its windows, quitting, opening the release page.</summary>
internal sealed class WpfUpdateHost : IUpdateHost
{
    /// <summary>
    /// Closes the windows one by one, as quitting does: an editor with an unsaved capture asks first, and keeping it open
    /// stops here.
    /// </summary>
    public bool TryCloseWindows()
    {
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            if (!closed)
                return false;
        }

        return true;
    }

    public void Quit() => Application.Current.Shutdown();

    public void OpenInBrowser(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
