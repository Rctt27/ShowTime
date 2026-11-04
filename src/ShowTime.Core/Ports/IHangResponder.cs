namespace ShowTime.Core.Ports;

/// <summary>What the app does when its UI thread stops answering (<see cref="Diagnostics.HangWatchdog"/>).</summary>
public interface IHangResponder
{
    /// <summary>
    /// The UI thread has not answered for <paramref name="silence"/>. Called off the UI thread, which cannot help; may
    /// block while the user decides, and may end the process.
    /// </summary>
    /// <param name="first">True for the first report of this hang, false when it is reported again.</param>
    void Hung(TimeSpan silence, bool first);

    /// <summary>The UI thread answers again, after <paramref name="silence"/>; called on it.</summary>
    void Recovered(TimeSpan silence);
}
