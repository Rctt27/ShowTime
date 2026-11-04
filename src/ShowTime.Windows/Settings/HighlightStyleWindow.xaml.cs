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
/// through a highlighter of its own over the recording's overlay, on settings of its own that the sliders change. It
/// draws nothing while a recording is under way: its stroke would be in the video.
/// </summary>
// The highlighter is released when the window closes, the end of a WPF window's life.
#pragma warning disable CA1001
public partial class HighlightStyleWindow : Window
#pragma warning restore CA1001
{
    private readonly AppSettings _preview;
    private readonly PreviewHighlightInput _input = new();
    private readonly Highlighter _highlighter;
    private readonly IRecordingStatus _recording;
    private readonly List<Slider> _sliders = [];
    private readonly List<TextBlock> _values = [];

    internal HighlightStyleWindow(HighlightStyle style, SettingsDefaults defaults, IHighlightOverlayFactory overlays, IUiThread uiThread,
        TimeProvider clock, IRecordingStatus recording)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Chosen = style;
        // The defaults' trigger is valid, so the highlighter enables whatever the user's is.
        _preview = new AppSettings(defaults) { HighlightStyle = style };
        _highlighter = new Highlighter(_preview, _input, overlays, uiThread, clock, HotkeyNaming.Windows);
        foreach (var field in HighlightStyle.Fields)
            AddSlider(field);
        _recording = recording;
        _recording.StateChanged += Recording_StateChanged;
        Closed += (_, _) =>
        {
            _recording.StateChanged -= Recording_StateChanged;
            _highlighter.Dispose();
        };
    }

    /// <summary>The style as the sliders set it; WPF's Window already has a Style.</summary>
    internal HighlightStyle Chosen { get; private set; }

    /// <summary>One per <see cref="HighlightStyle.Fields"/>, in their order.</summary>
    internal IReadOnlyList<Slider> Sliders => _sliders;

    /// <summary>The value shown beside each slider.</summary>
    internal IReadOnlyList<TextBlock> Values => _values;

    /// <summary>What a press in the zone does, over the area it covers in physical pixels.</summary>
    internal void Press(PixelRect zone)
    {
        if (_recording.IsBusy)
            return;
        // The overlay goes over the zone at each press, where the window is now.
        _highlighter.Enable(zone);
        _input.Press();
    }

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
        // As it is, even off the slider's step: the stroke keeps that value until the slider moves.
        value.Text = field.Format(field.Get(Chosen));
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
        _values.Add(value);
    }

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var topLeft = Zone.PointToScreen(new Point(0, 0));
        var bottomRight = Zone.PointToScreen(new Point(Zone.ActualWidth, Zone.ActualHeight));
        var left = (int)Math.Round(topLeft.X);
        var top = (int)Math.Round(topLeft.Y);
        Press(new PixelRect(left, top, (int)Math.Round(bottomRight.X) - left, (int)Math.Round(bottomRight.Y) - top));
        Zone.CaptureMouse();
        e.Handled = true;
    }

    private void Zone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Zone.ReleaseMouseCapture();

    /// <summary>A recording that starts while the preview draws ends its stroke at once.</summary>
    private void Recording_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Recording_StateChanged(sender, e));
            return;
        }
        if (_recording.IsBusy)
            _highlighter.Disable();
    }

    private void Defaults_Click(object sender, RoutedEventArgs e) => ResetToDefaults();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
