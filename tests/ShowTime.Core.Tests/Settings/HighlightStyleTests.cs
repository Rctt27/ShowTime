using ShowTime.Core.Settings;

namespace ShowTime.Core.Tests.Settings;

public sealed class HighlightStyleTests
{
    [Fact]
    public void The_default_is_the_stroke_chosen_in_the_prototype()
    {
        var s = HighlightStyle.Default;
        (s.Thickness, s.MinRatio, s.FastSpeed, s.WidthSmoothing, s.Fluidity).ShouldBe((7, 0.32, 3900, 180, 25));
        (s.StartTaper, s.EndTaper, s.TaperFloor, s.Opacity).ShouldBe((24, 48, 0.25, 0.45));
    }

    [Fact]
    public void Each_field_reads_and_writes_its_own_property_from_a_default_within_bounds()
    {
        HighlightStyle.Fields.Count.ShouldBe(9);
        foreach (var field in HighlightStyle.Fields)
        {
            field.Get(HighlightStyle.Default).ShouldBeInRange(field.Min, field.Max);
            var changed = field.With(HighlightStyle.Default, field.Max);
            field.Get(changed).ShouldBe(field.Max);
            HighlightStyle.Fields.Count(f => f.Get(changed) != f.Get(HighlightStyle.Default)).ShouldBe(1);
        }
    }

    [Fact]
    public void Clamped_brings_back_out_of_range_and_non_finite_values()
    {
        var s = new HighlightStyle { Thickness = -4, Opacity = 5, FastSpeed = double.PositiveInfinity, Fluidity = double.NaN }.Clamped();
        s.Thickness.ShouldBe(3);
        s.Opacity.ShouldBe(1);
        s.FastSpeed.ShouldBe(HighlightStyle.Default.FastSpeed);
        s.Fluidity.ShouldBe(HighlightStyle.Default.Fluidity);
        HighlightStyle.Default.Clamped().ShouldBe(HighlightStyle.Default);
    }

    [Theory]
    [InlineData(0, 7.26, 7.5)]
    [InlineData(1, 0.70000000000001, 0.7)]
    [InlineData(2, 3949, 3900)]
    [InlineData(8, 5, 1)]
    [InlineData(8, 0.45, 0.45)]
    public void A_slider_value_snaps_to_its_step_within_bounds(int field, double value, double snapped) =>
        HighlightStyle.Fields[field].Snap(value).ShouldBe(snapped);

    [Fact]
    public void Every_default_is_on_its_slider_step() =>
        HighlightStyle.Fields.ShouldAllBe(f => f.Snap(f.Get(HighlightStyle.Default)) == f.Get(HighlightStyle.Default));

    [Theory]
    [InlineData(0, 7, "7 px")]
    [InlineData(0, 7.5, "7,5 px")]
    [InlineData(1, 0.32, "32 %")]
    [InlineData(2, 3900, "3900 px/s")]
    [InlineData(3, 180, "180 ms")]
    public void Values_are_shown_with_their_unit(int field, double value, string text) =>
        HighlightStyle.Fields[field].Format(value).ShouldBe(text);
}
