using AppKit;
using CoreGraphics;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Mac.Platform;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac.Settings;

/// <summary>
/// Tunes the highlight's stroke with a preview, the same as on Windows: holding the mouse button in the zone draws
/// exactly as a recording does, through a highlighter of its own over the recording's overlay, on settings of its own
/// that the sliders change. Shown as a sheet of the settings window, not a modal session, so that the highlighter's
/// frames keep running.
/// </summary>
internal sealed class HighlightStyleWindowController : NSWindowController
{
    private const int EntryWidth = 200;

    private readonly AppSettings _preview;
    private readonly PreviewHighlightInput _input = new();
    private readonly Highlighter _highlighter;
    private readonly PreviewZone _zone = new();
    private readonly List<(HighlightStyleField Field, NSSlider Slider, NSTextField Value)> _sliders = [];

    public HighlightStyleWindowController(HighlightStyle style, SettingsDefaults defaults, IHighlightOverlayFactory overlays,
        IUiThread uiThread, TimeProvider clock) : base(CreateWindow())
    {
        Chosen = style;
        // The defaults' trigger is valid, so the highlighter enables whatever the user's is.
        _preview = new AppSettings(defaults) { HighlightStyle = style };
        _highlighter = new Highlighter(_preview, _input, overlays, uiThread, clock, HotkeyNaming.MacOS);
        _zone.Pressed += (_, _) => Press();
        var window = Window!;
        window.ContentView = Build();
        window.SetContentSize(window.ContentView.FittingSize);
    }

    /// <summary>The style as the sliders set it.</summary>
    public HighlightStyle Chosen { get; private set; }

    /// <summary>Closed with "OK".</summary>
    public bool Accepted { get; private set; }

    private static NSWindow CreateWindow()
    {
        var window = new NSWindow(new CGRect(0, 0, 700, 600), NSWindowStyle.Titled, NSBackingStore.Buffered, false)
        {
            Title = "Trait du surlignage"
        };
        window.ReleaseWhenClosed(false);
        return window;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _highlighter.Dispose();
        base.Dispose(disposing);
    }

    private NSStackView Build()
    {
        var hint = Help("Maintenez le bouton de la souris dans la zone et tracez : c'est le trait de l'enregistrement, sur un fond " +
            "clair et sur un fond sombre.", PreviewZone.Width);
        var rows = new List<NSView> { hint, _zone };
        var fields = HighlightStyle.Fields;
        for (var i = 0; i < fields.Count; i += 3)
            rows.Add(Horizontal([.. fields.Skip(i).Take(3).Select(Entry)], 20));

        var reset = NSButton.CreateButton("Valeurs par défaut", ResetToDefaults);
        var cancel = NSButton.CreateButton("Annuler", () => Finish(false));
        cancel.KeyEquivalent = "\u001B";
        var ok = NSButton.CreateButton("OK", () => Finish(true));
        ok.KeyEquivalent = "\r";
        rows.Add(Horizontal([reset, cancel, ok], 8));

        var form = NSStackView.FromViews([.. rows]);
        form.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        form.Alignment = NSLayoutAttribute.Leading;
        form.Spacing = 12;
        form.EdgeInsets = new NSEdgeInsets(16, 20, 16, 20);
        return form;
    }

    private NSStackView Entry(HighlightStyleField field)
    {
        var value = Help(field.Format(field.Snap(field.Get(Chosen))), EntryWidth);
        var slider = new NSSlider { MinValue = field.Min, MaxValue = field.Max, DoubleValue = field.Get(Chosen), Continuous = true };
        slider.WidthAnchor.ConstraintEqualTo(EntryWidth).Active = true;
        slider.Activated += (_, _) =>
        {
            var snapped = field.Snap(slider.DoubleValue);
            Chosen = field.With(Chosen, snapped);
            _preview.HighlightStyle = Chosen;
            value.StringValue = field.Format(snapped);
        };
        _sliders.Add((field, slider, value));

        var entry = NSStackView.FromViews([Horizontal([NSTextField.CreateLabel(field.Label), value], 8), slider, Help(field.Help, EntryWidth)]);
        entry.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        entry.Alignment = NSLayoutAttribute.Leading;
        entry.Spacing = 4;
        entry.WidthAnchor.ConstraintEqualTo(EntryWidth).Active = true;
        return entry;
    }

    private void ResetToDefaults()
    {
        Chosen = HighlightStyle.Default;
        _preview.HighlightStyle = Chosen;
        foreach (var (field, slider, value) in _sliders)
        {
            slider.DoubleValue = field.Get(Chosen);
            value.StringValue = field.Format(field.Get(Chosen));
        }
    }

    private void Press()
    {
        // The overlay goes over the zone at each press, where the window is now.
        _highlighter.Enable(ZoneInPixels());
        _input.Press();
    }

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        _highlighter.Disable();
        if (Window is { SheetParent: { } parent } sheet)
            parent.EndSheet(sheet);
    }

    /// <summary>The zone in ShowTime's pixels, on the screen holding its centre.</summary>
    private PixelRect ZoneInPixels()
    {
        var window = Window!;
        var frame = window.ConvertRectToScreen(_zone.ConvertRectToView(_zone.Bounds, window.ContentView));
        double x = frame.X + frame.Width / 2, y = frame.Y + frame.Height / 2;
        var screens = ScreenInfo.Current();
        var screen = screens.FirstOrDefault(s =>
            x >= s.Frame.X && x < s.Frame.X + s.Frame.Width && y >= s.Frame.Y && y < s.Frame.Y + s.Frame.Height) ?? screens[0];
        return MacScreenSpace.FromCocoa(screen.Frame, screen.Pixels, new ScreenFrame(frame.X, frame.Y, frame.Width, frame.Height, screen.Frame.Scale));
    }

    private static NSStackView Horizontal(NSView[] views, int spacing)
    {
        var row = NSStackView.FromViews(views);
        row.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        row.Alignment = NSLayoutAttribute.Top;
        row.Spacing = spacing;
        return row;
    }

    private static NSTextField Help(string text, int width)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.TextColor = NSColor.SecondaryLabel;
        if (NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize) is { } font)
            label.Font = font;
        label.PreferredMaxLayoutWidth = width;
        return label;
    }

    /// <summary>The zone the preview draws over: a light half and a dark half, as an application's screens.</summary>
    private sealed class PreviewZone : NSView
    {
        public const int Width = 660;
        private const int Height = 240;

        public PreviewZone() : base(new CGRect(0, 0, Width, Height))
        {
            AddHalf(0, NSColor.White, Color(0x1C2230), Color(0x5D6676), Color(0x2F6FDE), NSColor.White,
                "Nouvelle page de paiement",
                "Le parcours passe de cinq écrans à deux. Le récapitulatif de commande reste visible pendant la saisie de la carte.",
                "Publier la page");
            AddHalf(Width / 2, Color(0x1D2028), Color(0xE6E9EF), Color(0x98A1B0), Color(0x6B9BFF), Color(0x0E1320),
                "Tableau de bord", "Le temps moyen passe à 48 secondes, et le taux d'abandon au paiement baisse de 31 %.",
                "Prévisualiser");
            // Above the labels, so that a press anywhere in the zone reaches it.
            var catcher = new PressCatcher(new CGRect(0, 0, Width, Height));
            catcher.Pressed += (_, _) => Pressed?.Invoke(this, EventArgs.Empty);
            AddSubview(catcher);
            WidthAnchor.ConstraintEqualTo(Width).Active = true;
            HeightAnchor.ConstraintEqualTo(Height).Active = true;
        }

        public event EventHandler? Pressed;

        public override bool IsFlipped => true;

        private void AddHalf(int x, NSColor background, NSColor title, NSColor text, NSColor button, NSColor buttonText,
            string heading, string body, string action)
        {
            AddSubview(new Fill(new CGRect(x, 0, Width / 2, Height), background, 0));
            AddSubview(Label(NSTextField.CreateLabel(heading), title, NSFont.BoldSystemFontOfSize(17), new CGRect(x + 20, 20, Width / 2 - 40, 24)));
            AddSubview(Label(NSTextField.CreateWrappingLabel(body), text, NSFont.SystemFontOfSize(13), new CGRect(x + 20, 54, Width / 2 - 40, 60)));
            AddSubview(new Fill(new CGRect(x + 20, 130, 130, 30), button, 6));
            AddSubview(Label(NSTextField.CreateLabel(action), buttonText, NSFont.BoldSystemFontOfSize(13), new CGRect(x + 32, 136, 110, 18)));
        }

        private static NSTextField Label(NSTextField label, NSColor color, NSFont? font, CGRect frame)
        {
            label.TextColor = color;
            if (font is not null)
                label.Font = font;
            label.Frame = frame;
            return label;
        }

        private static NSColor Color(int rgb) =>
            NSColor.FromSrgb((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }

    /// <summary>A plain rectangle, rounded or not.</summary>
    private sealed class Fill(CGRect frame, NSColor color, int radius) : NSView(frame)
    {
        public override void DrawRect(CGRect dirtyRect)
        {
            color.SetFill();
            NSBezierPath.FromRoundedRect(Bounds, radius, radius).Fill();
        }
    }

    /// <summary>Takes the presses over the zone, which its labels would otherwise receive.</summary>
    private sealed class PressCatcher(CGRect frame) : NSView(frame)
    {
        public event EventHandler? Pressed;

        public override void MouseDown(NSEvent theEvent) => Pressed?.Invoke(this, EventArgs.Empty);
    }
}
