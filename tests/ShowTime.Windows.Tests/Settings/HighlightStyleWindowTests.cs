using System.Windows.Threading;
using ShowTime.Adapters;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime.Windows.Tests.Settings;

public sealed class HighlightStyleWindowTests
{
    private static readonly PixelRect Zone = new(0, 0, 200, 100);

    private readonly FakeRecordingStatus _recording = new();
    private readonly FakeOverlays _overlays = new();

    private HighlightStyleWindow Create(HighlightStyle style, IHighlightOverlayFactory? overlays = null) =>
        new(style, SettingsDefaults.ForWindows(@"C:\Out"), overlays ?? new HighlightOverlayFactory(),
            new DispatcherUiThread(Dispatcher.CurrentDispatcher), TimeProvider.System, _recording);

    [Fact]
    public async Task Each_field_gets_a_slider_with_its_bounds_and_value() => await Sta.Run(() =>
    {
        var window = Create(HighlightStyle.Default with { Thickness = 12 });
        window.Sliders.Count.ShouldBe(HighlightStyle.Fields.Count);
        window.Sliders[0].Minimum.ShouldBe(3);
        window.Sliders[0].Maximum.ShouldBe(40);
        window.Sliders[0].Value.ShouldBe(12);
        window.Close();
    });

    [Fact]
    public async Task A_value_off_its_step_shows_as_it_is() => await Sta.Run(() =>
    {
        // Written by hand: the stroke keeps 7.3 until the slider moves, and so does its label.
        var window = Create(HighlightStyle.Default with { Thickness = 7.3 });
        window.Values[0].Text.ShouldBe("7,3 px");
        window.Close();
    });

    [Fact]
    public async Task Moving_a_slider_changes_the_style() => await Sta.Run(() =>
    {
        var window = Create(HighlightStyle.Default);
        window.Sliders[8].Value = 0.7;
        window.Chosen.ShouldBe(HighlightStyle.Default with { Opacity = 0.7 });
        window.Values[8].Text.ShouldBe("70 %");
        window.Close();
    });

    [Fact]
    public async Task Defaults_restore_the_default_style() => await Sta.Run(() =>
    {
        var window = Create(HighlightStyle.Default with { Thickness = 20, Fluidity = 60 });
        window.Sliders[1].Value = 0.8;

        window.ResetToDefaults();
        window.Chosen.ShouldBe(HighlightStyle.Default);
        window.Sliders[0].Value.ShouldBe(7);
        window.Close();
    });

    [Fact]
    public async Task A_press_while_a_recording_is_under_way_draws_nothing() => await Sta.Run(() =>
    {
        _recording.IsBusy = true;
        var window = Create(HighlightStyle.Default, _overlays);
        window.Press(Zone);
        _overlays.Created.ShouldBe(0);
        window.Close();
    });

    [Fact]
    public async Task A_recording_that_starts_ends_the_preview_stroke() => await Sta.Run(() =>
    {
        var window = Create(HighlightStyle.Default, _overlays);
        window.Press(Zone);
        _overlays.Created.ShouldBe(1);

        _recording.IsBusy = true;
        _recording.Raise();
        _overlays.Last!.Disposed.ShouldBeTrue();
        window.Close();
    });

    private sealed class FakeRecordingStatus : IRecordingStatus
    {
        public bool IsBusy { get; set; }
        public bool IsRecording => IsBusy;
        public event EventHandler? StateChanged;

        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeOverlays : IHighlightOverlayFactory
    {
        public int Created { get; private set; }
        public FakeOverlay? Last { get; private set; }

        public IHighlightOverlay Create(PixelRect area)
        {
            Created++;
            return Last = new FakeOverlay();
        }
    }

    private sealed class FakeOverlay : IHighlightOverlay
    {
        public bool Disposed { get; private set; }
        public double Scale => 1;

        public void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed) { }
        public void SetOpacity(double opacity) { }
        public void Hide() { }
        public void Dispose() => Disposed = true;
    }
}
