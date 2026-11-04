using Microsoft.Extensions.Time.Testing;
using ShowTime.Core.Diagnostics;
using ShowTime.Core.Ports;

namespace ShowTime.Core.Tests.Diagnostics;

/// <summary>The watchdog pings the UI thread every 2 s and reports 10 s without an answer.</summary>
public sealed class HangWatchdogTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly DateTimeOffset _start;
    private readonly ManualUiThread _ui = new();
    private readonly FakeHangResponder _responder = new();
    private HangWatchdog? _watchdog;

    public HangWatchdogTests() => _start = _clock.GetUtcNow();

    public void Dispose() => _watchdog?.Dispose();

    private HangWatchdog Start(IUiThread? ui = null)
    {
        _watchdog = new HangWatchdog(_clock, ui ?? _ui, _responder);
        _watchdog.Start();
        return _watchdog;
    }

    /// <summary>Moves the clock, a second at a time, to <paramref name="seconds"/> after the start.</summary>
    private void RunTo(int seconds, bool uiAnswers = false)
    {
        while (_clock.GetUtcNow() - _start < TimeSpan.FromSeconds(seconds))
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            if (uiAnswers)
                _ui.RunAll();
        }
    }

    [Fact]
    public void A_ui_thread_that_answers_is_never_reported()
    {
        Start(new ImmediateUiThread());

        RunTo(60);

        _responder.Hangs.ShouldBeEmpty();
        _responder.Recoveries.ShouldBeEmpty();
    }

    [Fact]
    public void An_answer_before_ten_seconds_reports_nothing()
    {
        Start();

        RunTo(8);
        _ui.RunAll();
        RunTo(30, uiAnswers: true);

        _responder.Hangs.ShouldBeEmpty();
        _responder.Recoveries.ShouldBeEmpty();
    }

    [Fact]
    public void Ten_seconds_without_an_answer_is_reported_once()
    {
        Start();

        // The first ping leaves at 2 s.
        RunTo(10);
        _responder.Hangs.ShouldBeEmpty();

        RunTo(12);
        _responder.Hangs.ShouldBe([(TimeSpan.FromSeconds(10), true)]);

        RunTo(40);
        _responder.Hangs.Count.ShouldBe(1);
    }

    [Fact]
    public void A_hang_still_there_is_reported_again_after_thirty_seconds()
    {
        Start();

        RunTo(42);

        _responder.Hangs.Count.ShouldBe(2);
        _responder.Hangs[1].ShouldBe((TimeSpan.FromSeconds(40), false));
    }

    [Fact]
    public void An_answer_after_a_report_is_reported_as_recovered_and_a_new_hang_is_first_again()
    {
        Start();
        RunTo(13);

        _ui.RunAll();

        _responder.Recoveries.ShouldBe([TimeSpan.FromSeconds(11)]);
        // The next ping leaves at 14 s.
        RunTo(24);
        _responder.Hangs.Count.ShouldBe(2);
        _responder.Hangs[1].ShouldBe((TimeSpan.FromSeconds(10), true));
    }

    [Fact]
    public void A_report_still_open_is_not_reported_again()
    {
        Start();
        // The user takes 20 s to answer the dialog.
        _responder.OnHung = () => _clock.Advance(TimeSpan.FromSeconds(20));

        RunTo(40);

        _responder.Hangs.Count.ShouldBe(1);
    }

    [Fact]
    public void Disposed_it_posts_and_reports_nothing_more()
    {
        var watchdog = Start();
        RunTo(4);

        watchdog.Dispose();
        RunTo(60);

        _ui.Pending.ShouldBe(1);
        _responder.Hangs.ShouldBeEmpty();
    }
}
