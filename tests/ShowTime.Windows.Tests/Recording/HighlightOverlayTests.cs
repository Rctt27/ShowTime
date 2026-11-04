using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Abstractions;
using System.Windows;
using ShowTime.Adapters;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Infrastructure.Platform;
using ShowTime.Infrastructure.Recording;
using ShowTime.Platform.Windows;
using Brushes = System.Windows.Media.Brushes;

namespace ShowTime.Windows.Tests.Recording;

[Collection(ScreenRecordings.Name)]
public sealed class HighlightOverlayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"showtime-highlight-{Guid.NewGuid():N}.mp4");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task The_overlay_is_click_through_never_activated_and_captured() => await Sta.Run(() =>
    {
        using var overlay = new HighlightOverlay(new Rectangle(0, 0, 200, 100));
        overlay.SetOpacity(HighlightTrail.FullOpacity);
        overlay.Draw([new PixelPoint(10, 50), new PixelPoint(190, 50)]);

        overlay.Visible.ShouldBeTrue();
        overlay.TrailOpacity.ShouldBe(HighlightTrail.FullOpacity, 0.01);
        HighlightOverlay.Thickness.ShouldBe(14.4f);
        const int expected = Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        (Native.GetWindowLong(overlay.Handle, Native.GWL_EXSTYLE) & expected).ShouldBe(expected);
        Native.GetWindowDisplayAffinity(overlay.Handle, out var affinity).ShouldBeTrue();
        // WDA_NONE: unlike the recording frame, the trail must be in the video.
        affinity.ShouldBe(0u);
    });

    [Fact]
    public async Task Opacity_changes_and_hiding_hides_the_trail() => await Sta.Run(() =>
    {
        using var overlay = new HighlightOverlay(new Rectangle(0, 0, 200, 100));
        overlay.Draw([new PixelPoint(10, 50)]);
        overlay.SetOpacity(0.2);
        overlay.TrailOpacity.ShouldBe(0.2, 0.01);
        ((IHighlightOverlay)overlay).Hide();
        overlay.Visible.ShouldBeFalse();
    });

    [Fact]
    public async Task The_trail_is_antialiased_without_a_fringe() => await Sta.Run(() =>
    {
        using var overlay = new HighlightOverlay(new Rectangle(0, 0, 200, 200));
        overlay.Draw([new PixelPoint(20, 30), new PixelPoint(180, 170)]);

        var pixels = (from x in Enumerable.Range(0, 200) from y in Enumerable.Range(0, 200) select overlay.SurfacePixel(x, y)).ToList();
        pixels.ShouldContain(c => c.A == 255);
        pixels.ShouldContain(c => c.A > 0 && c.A < 255);
        // Every visible pixel is the ink: no blend with a key color, so no fringe.
        pixels.Where(c => c.A >= 64).ShouldAllBe(c => c.R >= 250 && Math.Abs(c.G - 212) <= 6 && c.B <= 6);
    });

    [Fact]
    public async Task Hiding_clears_the_surface() => await Sta.Run(() =>
    {
        using var overlay = new HighlightOverlay(new Rectangle(0, 0, 100, 100));
        overlay.Draw([new PixelPoint(10, 50), new PixelPoint(90, 50)]);
        ((IHighlightOverlay)overlay).Hide();

        (from x in Enumerable.Range(0, 100) from y in Enumerable.Range(0, 100) select overlay.SurfacePixel(x, y))
            .ShouldAllBe(c => c.A == 0);
    });

    [Fact]
    public async Task Points_outside_the_area_are_accepted() => await Sta.Run(() =>
    {
        using var overlay = new HighlightOverlay(new Rectangle(100, 100, 200, 100));
        overlay.Draw([new PixelPoint(-500, -500), new PixelPoint(5000, 5000)]);
        Sta.DoEvents();
        overlay.Visible.ShouldBeTrue();
    });

    [Fact]
    public async Task The_factory_places_the_overlay_on_the_area() => await Sta.Run(() =>
    {
        using var overlay = (HighlightOverlay)new HighlightOverlayFactory().Create(new PixelRect(30, 40, 200, 100));
        overlay.Draw([new PixelPoint(50, 50)]);
        overlay.Bounds.ShouldBe(new Rectangle(30, 40, 200, 100));
    });

    /// <summary>
    /// A black window, the overlay over it with a trail near the top, recorded as the app records. Whatever the screen's
    /// scale, yellow must appear in the video, and nowhere in the top rows, above the trail.
    /// </summary>
    [Fact]
    public async Task The_trail_is_in_the_video()
    {
        var ffmpeg = AppPaths.CreateFfmpegLocator(new FileSystem()).Find("");
        Assert.SkipUnless(ffmpeg is not null, "Needs ffmpeg.");
        var area = new PixelRect(0, 0, 320, 240);

        var (window, overlay) = await Sta.Run(() =>
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = 0,
                Top = 0,
                Width = 320,
                Height = 240,
                Background = Brushes.Black,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            w.Show();
            var o = new HighlightOverlay(area.ToRectangle());
            o.SetOpacity(HighlightTrail.FullOpacity);
            o.Draw([new PixelPoint(20, 60), new PixelPoint(150, 60)]);
            Sta.DoEvents();
            return (w, o);
        });

        try
        {
            var source = new WasapiSystemAudio();
            var factory = new FfmpegVideoRecorderFactory(new DirectShowGdigrab(() => false), source);
            using var recorder = factory.Start(ffmpeg!, area, _path, new VideoOptions(30, null, false), _ => { });
            (await recorder.Capturing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).ShouldBeTrue(recorder.ErrorTail);
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            (await recorder.StopAsync()).ShouldBeTrue(recorder.ErrorTail);
        }
        finally
        {
            await Sta.Run(() =>
            {
                overlay.Dispose();
                window.Close();
            });
        }

        var frame = await FrameAsync(ffmpeg!, area);
        int Yellow(int fromRow, int toRow)
        {
            var count = 0;
            for (var y = fromRow; y < toRow; y++)
            {
                for (var x = 0; x < area.Width; x++)
                {
                    var i = (y * area.Width + x) * 3;
                    // 25 % of #FFD400 over black is about (64, 53, 0).
                    if (frame[i] > 40 && frame[i + 1] > 30 && frame[i + 2] < 30)
                        count++;
                }
            }
            return count;
        }
        Yellow(0, area.Height).ShouldBeGreaterThan(100);
        Yellow(0, 20).ShouldBe(0);
    }

    /// <summary>The frame at 1 s, as RGB bytes.</summary>
    private async Task<byte[]> FrameAsync(string ffmpeg, PixelRect area)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-ss", "1", "-i", _path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes);
        await process.WaitForExitAsync();
        bytes.Length.ShouldBe(area.Width * area.Height * 3, await error);
        return bytes.ToArray();
    }
}
