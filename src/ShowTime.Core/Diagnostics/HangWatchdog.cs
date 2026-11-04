using ShowTime.Core.Ports;

namespace ShowTime.Core.Diagnostics;

/// <summary>
/// Notices a UI thread that stops answering, so that a frozen ShowTime never costs a reboot: every
/// <see cref="Interval"/> it posts a ping to the UI thread, and reports to <see cref="IHangResponder"/> a ping left
/// unanswered for <see cref="Threshold"/>, again every <see cref="AskAgainAfter"/> while it lasts, then its end.
/// </summary>
/// <remarks>
/// Time is measured with <see cref="TimeProvider.GetTimestamp"/>, not the wall clock: on a Mac it is the uptime, which
/// stops while the computer sleeps, so waking up is not a hang. A report blocks while the user decides: the ticks in
/// the meantime report nothing.
/// </remarks>
public sealed class HangWatchdog(TimeProvider clock, IUiThread uiThread, IHangResponder responder) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long? _pingSentAt;
    private long? _lastReportEnded;
    private bool _reported;
    private bool _reporting;
    private bool _disposed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;
            _timer = clock.CreateTimer(_ => Tick(), null, Interval, Interval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        TimeSpan silence;
        bool first;
        lock (_gate)
        {
            if (_disposed || _reporting)
                return;
            var now = clock.GetTimestamp();
            if (_pingSentAt is not { } sentAt)
            {
                _pingSentAt = now;
                silence = TimeSpan.Zero;
                first = false;
            }
            else
            {
                silence = clock.GetElapsedTime(sentAt, now);
                if (silence < Threshold ||
                    (_lastReportEnded is { } ended && clock.GetElapsedTime(ended, now) < AskAgainAfter))
                    return;
                first = !_reported;
                _reported = true;
                _reporting = true;
            }
        }

        // Outside the lock: the UI thread may run the ping at once, and a report blocks while the user decides.
        if (silence == TimeSpan.Zero)
        {
            uiThread.Post(Answer);
            return;
        }
        try
        {
            responder.Hung(silence, first);
        }
        finally
        {
            lock (_gate)
            {
                _reporting = false;
                // Answered while the user was deciding: the next hang is a new one, reported after the threshold.
                _lastReportEnded = _reported ? clock.GetTimestamp() : null;
            }
        }
    }

    /// <summary>The ping, run on the UI thread: it answers.</summary>
    private void Answer()
    {
        TimeSpan silence;
        bool recovered;
        lock (_gate)
        {
            if (_pingSentAt is not { } sentAt)
                return;
            silence = clock.GetElapsedTime(sentAt);
            recovered = _reported;
            _pingSentAt = null;
            _reported = false;
            _lastReportEnded = null;
        }
        if (recovered)
            responder.Recovered(silence);
    }
}
