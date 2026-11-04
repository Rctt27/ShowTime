using System.Windows.Threading;
using ShowTime.Adapters;
using ShowTime.Core.Settings;

namespace ShowTime.Windows.Tests.Settings;

public sealed class HighlightStyleWindowTests
{
    private static HighlightStyleWindow Create(HighlightStyle style) =>
        new(style, SettingsDefaults.ForWindows(@"C:\Out"), new HighlightOverlayFactory(), new DispatcherUiThread(Dispatcher.CurrentDispatcher),
            TimeProvider.System);

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
    public async Task Moving_a_slider_changes_the_style() => await Sta.Run(() =>
    {
        var window = Create(HighlightStyle.Default);
        window.Sliders[8].Value = 0.7;
        window.Chosen.ShouldBe(HighlightStyle.Default with { Opacity = 0.7 });
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
}
