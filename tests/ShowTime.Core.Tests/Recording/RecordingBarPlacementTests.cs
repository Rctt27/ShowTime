using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Tests.Recording;

public sealed class RecordingBarPlacementTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);
    private static readonly PixelSize Bar = new(400, 50);

    [Fact]
    public void Below_the_area_and_centred_when_there_is_room() =>
        RecordingBarPlacement.Place(new PixelRect(500, 100, 600, 400), Bar, WorkArea).ShouldBe(new PixelPoint(600, 510));

    [Fact]
    public void Above_the_area_when_it_reaches_the_bottom() =>
        RecordingBarPlacement.Place(new PixelRect(500, 600, 600, 420), Bar, WorkArea).ShouldBe(new PixelPoint(600, 540));

    [Fact]
    public void Inside_the_bottom_of_the_screen_when_the_area_fills_it() =>
        RecordingBarPlacement.Place(WorkArea, Bar, WorkArea).ShouldBe(new PixelPoint(760, 980));

    [Fact]
    public void Kept_on_the_screen_horizontally() =>
        RecordingBarPlacement.Place(new PixelRect(-50, 100, 100, 100), Bar, WorkArea).X.ShouldBe(0);

    [Fact]
    public void A_bar_wider_than_the_screen_sticks_to_its_left_edge() =>
        RecordingBarPlacement.Place(new PixelRect(0, 100, 100, 100), new PixelSize(3000, 50), WorkArea).X.ShouldBe(0);

    private static readonly PixelRect Left = new(0, 0, 1920, 1080);
    private static readonly PixelRect LeftWork = new(0, 25, 1920, 1000);
    private static readonly PixelRect RightWork = new(1920, 0, 2560, 1400);

    [Fact]
    public void Next_to_an_area_on_its_own_screen_and_clear_of_it()
    {
        var placement = RecordingBarPlacement.Place(new PixelRect(500, 100, 600, 400), [new BarScreen(LeftWork, Bar), new BarScreen(RightWork, Bar)]);
        placement.ShouldBe(new BarPlacement(0, new PixelPoint(600, 510), CoversArea: false));
    }

    [Fact]
    public void On_another_screen_when_the_area_leaves_no_room_on_its_own()
    {
        // The bar would otherwise sit in the picture: macOS no longer keeps a window out of a screen recording.
        var other = new PixelSize(800, 100);
        var placement = RecordingBarPlacement.Place(Left, [new BarScreen(LeftWork, Bar), new BarScreen(RightWork, other)]);
        placement.ShouldBe(new BarPlacement(1, new PixelPoint(1920 + 880, 1290), CoversArea: false));
    }

    [Fact]
    public void Inside_the_area_with_a_single_screen()
    {
        var placement = RecordingBarPlacement.Place(Left, [new BarScreen(LeftWork, Bar)]);
        placement.ShouldBe(new BarPlacement(0, RecordingBarPlacement.Place(Left, Bar, LeftWork), CoversArea: true));
    }

    [Fact]
    public void Inside_the_area_on_its_own_screen_when_it_spans_every_screen()
    {
        var both = new PixelRect(0, 0, 1920 + 2560, 1400);
        var placement = RecordingBarPlacement.Place(both, [new BarScreen(LeftWork, Bar), new BarScreen(RightWork, Bar)]);
        placement.ShouldBe(new BarPlacement(0, RecordingBarPlacement.Place(both, Bar, LeftWork), CoversArea: true));
    }

    [Fact]
    public void Touching_the_area_without_entering_it_does_not_cover_it()
    {
        // Right and Bottom are exclusive: a bar starting on the area's Bottom row is outside it.
        var area = new PixelRect(0, 0, 1920, 965);
        var placement = RecordingBarPlacement.Place(area, [new BarScreen(LeftWork, Bar)], gap: 0);
        placement.ShouldBe(new BarPlacement(0, new PixelPoint(760, 965), CoversArea: false));
    }
}
