using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime;

/// <summary>
/// Tunes the highlight's stroke with a preview: holding the left button in the zone draws exactly as a recording does,
/// through a highlighter of its own over the recording's overlay, on settings of its own that the sliders change.
/// </summary>
// The highlighter is released when the window closes, the end of a WPF window's life.
#pragma warning disable CA1001
public partial class HighlightStyleWindow : Window
#pragma warning restore CA1001
{
    private readonly AppSettings _preview;
    private readonly PreviewHighlightInput _input = new();
    private readonly Highlighter _highlighter;
    private readonly List<Slider> _sliders = [];

    internal HighlightStyleWindow(HighlightStyle style, SettingsDefaults defaults, IHighlightOverlayFactory overlays, IUiThread uiThread,
        TimeProvider clock)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Chosen = style;
        // The defaults' trigger is valid, so the highlighter enables whatever the user's is.
        _preview = new AppSettings(defaults) { HighlightStyle = style };
        _highlighter = new Highlighter(_preview, _input, overlays, uiThread, clock, HotkeyNaming.Windows);
        foreach (var field in HighlightStyle.Fields)
            AddSlider(field);
        Closed += (_, _) => _highlighter.Dispose();
    }

    /// <summary>The style as the sliders set it; WPF's Window already has a Style.</summary>
    internal HighlightStyle Chosen { get; private set; }

    /// <summary>One per <see cref="HighlightStyle.Fields"/>, in their order.</summary>
    internal IReadOnlyList<Slider> Sliders => _sliders;

    /// <summary>What "Valeurs par défaut" does.</summary>
    internal void ResetToDefaults()
    {
        for (var i = 0; i < _sliders.Count; i++)
            _sliders[i].Value = HighlightStyle.Fields[i].Get(HighlightStyle.Default);
        Chosen = HighlightStyle.Default;
        _preview.HighlightStyle = Chosen;
    }

    private void AddSlider(HighlightStyleField field)
    {
        var value = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(value, Dock.Right);
        var title = new DockPanel();
        title.Children.Add(value);
        title.Children.Add(new TextBlock { Text = field.Label });

        var slider = new Slider
        {
            Minimum = field.Min,
            Maximum = field.Max,
            Value = field.Get(Chosen),
            SmallChange = field.Step,
            LargeChange = field.Step * 10,
            TickFrequency = field.Step,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 4, 0, 2)
        };
        value.Text = field.Format(field.Snap(slider.Value));
        slider.ValueChanged += (_, _) =>
        {
            Chosen = field.With(Chosen, field.Snap(slider.Value));
            _preview.HighlightStyle = Chosen;
            value.Text = field.Format(field.Snap(slider.Value));
        };

        var entry = new StackPanel { Margin = new Thickness(0, 6, 14, 6) };
        entry.Children.Add(title);
        entry.Children.Add(slider);
        entry.Children.Add(new TextBlock { Text = field.Help, Style = (Style)FindResource("Hint") });
        SliderPanel.Children.Add(entry);
        _sliders.Add(slider);
    }

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The overlay goes over the zone at each press, where the window is now, in physical pixels.
        var topLeft = Zone.PointToScreen(new Point(0, 0));
        var bottomRight = Zone.PointToScreen(new Point(Zone.ActualWidth, Zone.ActualHeight));
        var left = (int)Math.Round(topLeft.X);
        var top = (int)Math.Round(topLeft.Y);
        _highlighter.Enable(new PixelRect(left, top, (int)Math.Round(bottomRight.X) - left, (int)Math.Round(bottomRight.Y) - top));
        _input.Press();
        Zone.CaptureMouse();
        e.Handled = true;
    }

    private void Zone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Zone.ReleaseMouseCapture();

    private void Defaults_Click(object sender, RoutedEventArgs e) => ResetToDefaults();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
