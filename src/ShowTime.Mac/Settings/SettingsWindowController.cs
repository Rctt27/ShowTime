using System.Globalization;
using AppKit;
using CoreGraphics;
using Foundation;
using ShowTime.Core.Files;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Core.Transcription;
using ShowTime.Mac.Adapters;

namespace ShowTime.Mac.Settings;

/// <summary>What the settings window needs from the rest of the app.</summary>
internal sealed record SettingsContext(
    AppSettings Settings, SettingsDefaults Defaults, FfmpegLocator Locator, IFfmpegPlatform Platform,
    IAudioDeviceLister AudioDevices, IStartupRegistration Startup, SettingsEditor Editor, TranscriptionModelLocator Models,
    IHighlightOverlayFactory Overlays, IUiThread UiThread, TimeProvider Clock, IRecordingStatus Recording);

/// <summary>
/// The settings, the same fields as on Windows. The checks and the saving are <see cref="SettingsEditor"/>'s; the
/// window fills a <see cref="SettingsForm"/> and shows the message it returns.
/// </summary>
internal sealed class SettingsWindowController : NSWindowController
{
    /// <summary>The title bar and some room around the window, out of the screen's visible height.</summary>
    private const double TitleAndMargin = 60;
    /// <summary>The smallest height the window can be resized to.</summary>
    private const double MinHeight = 300;

    private const int LabelWidth = 170;

    private static readonly (string Title, AfterCaptureAction Action)[] AfterCaptureChoices =
    [
        ("Ouvrir l'éditeur d'annotations", AfterCaptureAction.OpenEditor),
        ("Enregistrer directement dans le dossier", AfterCaptureAction.SaveToFile),
        ("Copier dans le presse-papiers", AfterCaptureAction.CopyToClipboard),
        ("Enregistrer et copier dans le presse-papiers", AfterCaptureAction.SaveAndCopy)
    ];

    private readonly SettingsContext _context;
    private readonly NSTextField _folder = new();
    private readonly NSTextField _pattern = new();
    private readonly NSTextField _patternPreview = Help("");
    private readonly NSPopUpButton _format = new(CGRect.Empty, false);
    private readonly NSSlider _quality = new() { MinValue = 1, MaxValue = 100, Continuous = true };
    private readonly NSTextField _qualityValue = NSTextField.CreateLabel("");
    private readonly NSButton _copyPath = NSButton.CreateCheckbox("Copier le chemin du fichier dans le presse-papiers après l'enregistrement", () => { });
    private readonly NSPopUpButton _afterCapture = new(CGRect.Empty, false);
    private readonly NSTextField _ffmpeg = new();
    private readonly NSTextField _ffmpegStatus = Help("");
    private readonly NSPopUpButton _microphone = new(CGRect.Empty, false);
    private readonly NSButton _recordMicrophone = NSButton.CreateCheckbox("Enregistrer le micro", () => { });
    private readonly NSButton _systemAudio = NSButton.CreateCheckbox("Enregistrer le son de l'ordinateur", () => { });
    private readonly NSPopUpButton _frameRate = new(CGRect.Empty, false);
    private readonly NSButton _transcribe = NSButton.CreateCheckbox("Transcrire le micro en sous-titres après l'enregistrement", () => { });
    private readonly NSPopUpButton _language = new(CGRect.Empty, false);
    private readonly NSTextField _model = new();
    private readonly NSTextField _modelStatus = Help("");
    private readonly HotkeyField[] _hotkeys;
    private readonly HotkeyField _highlightTrigger;
    private readonly NSTextField _highlightWarning = Help("Pendant l'enregistrement, cette touche n'écrira plus rien dans les applications.");
    private readonly NSButton _startup = NSButton.CreateCheckbox("Lancer ShowTime à l'ouverture de session", () => { });
    private readonly NSButton _highlightStyleButton;
    private HighlightStyle _highlightStyle;
    private int _audioLoadGeneration;

    public SettingsWindowController(SettingsContext context) : base(CreateWindow())
    {
        _context = context;
        var settings = context.Settings;

        _folder.StringValue = settings.OutputFolder;
        _pattern.StringValue = settings.FileNamePattern;
        _pattern.Changed += (_, _) => UpdatePatternPreview();
        _format.AddItems(["PNG", "JPEG"]);
        _format.SelectItem(settings.Format == "jpg" ? 1 : 0);
        _format.Activated += (_, _) => UpdateFormat();
        _quality.IntValue = settings.JpegQuality;
        _quality.Activated += (_, _) => _qualityValue.IntValue = _quality.IntValue;
        _qualityValue.IntValue = settings.JpegQuality;
        _copyPath.State = settings.CopyPathAfterSave ? NSCellStateValue.On : NSCellStateValue.Off;
        _afterCapture.AddItems([.. AfterCaptureChoices.Select(c => c.Title)]);
        _afterCapture.SelectItem(Math.Max(0, Array.FindIndex(AfterCaptureChoices, c => c.Action == settings.AfterCapture)));

        _ffmpeg.StringValue = settings.FfmpegPath;
        _ffmpeg.PlaceholderString = "Laisser vide pour le chercher à côté de ShowTime, dans le PATH puis dans Homebrew";
        _ffmpeg.Changed += (_, _) => UpdateFfmpegStatus();
        _recordMicrophone.State = settings.RecordMicrophone ? NSCellStateValue.On : NSCellStateValue.Off;
        _systemAudio.State = settings.RecordSystemAudio ? NSCellStateValue.On : NSCellStateValue.Off;
        _frameRate.AddItems([.. SettingsForm.FrameRates.Select(f => f.ToString(CultureInfo.InvariantCulture))]);
        var rate = SettingsForm.FrameRates.OrderBy(f => Math.Abs(f - settings.VideoFrameRate)).First();
        _frameRate.SelectItem(SettingsForm.FrameRates.ToList().IndexOf(rate));
        _transcribe.State = settings.TranscribeMicrophone ? NSCellStateValue.On : NSCellStateValue.Off;
        _language.AddItems([.. SettingsForm.TranscriptionLanguages.Select(l => l.Name)]);
        _language.SelectItem(Math.Max(0, SettingsForm.TranscriptionLanguages.ToList().FindIndex(l => l.Code == settings.TranscriptionLanguage)));
        _model.StringValue = settings.TranscriptionModelPath;
        _model.PlaceholderString = "Laisser vide pour utiliser celui livré avec ShowTime";
        _model.Changed += (_, _) => UpdateModelStatus();
        UpdateModelStatus();

        _hotkeys =
        [
            new(settings.HotkeyRegion), new(settings.HotkeyWindow), new(settings.HotkeyFullScreen), new(settings.HotkeyLastRegion),
            new(settings.HotkeyVideoRegion), new(settings.HotkeyVideoFullScreen), new(settings.HotkeyVideoStart), new(settings.HotkeyVideoStop)
        ];
        // The highlight trigger is only registered while recording: any key may be held alone, Space included.
        _highlightTrigger = new HotkeyField(settings.HighlightTrigger) { AllowPlainKey = true };
        _highlightTrigger.ShortcutChanged += (_, _) => UpdateHighlightWarning();
        UpdateHighlightWarning();
        _highlightStyle = settings.HighlightStyle;
        _highlightStyleButton = NSButton.CreateButton("Personnaliser le trait…", EditHighlightStyle);
        // Its preview draws over the screen like a recording: not while one is under way, which may start from the menu
        // bar while this window is open.
        UpdateHighlightStyleButton();
        context.Recording.StateChanged += Recording_StateChanged;
        _startup.State = context.Startup.IsEnabled ? NSCellStateValue.On : NSCellStateValue.Off;

        var window = Window!;
        var form = Build();
        var size = form.FittingSize;
        window.ContentView = Scrollable(form);
        // No taller than the screen: the form scrolls, so that its last rows and the buttons stay reachable.
        var screen = (NSScreen.MainScreen?.VisibleFrame.Height ?? size.Height) - TitleAndMargin;
        window.SetContentSize(new CGSize(size.Width, Math.Min(size.Height, screen)));
        window.ContentMinSize = new CGSize(size.Width, Math.Min(size.Height, MinHeight));
        window.ContentMaxSize = new CGSize(size.Width, size.Height);
        window.WillClose += (_, _) =>
        {
            _context.Recording.StateChanged -= Recording_StateChanged;
            Closed?.Invoke(this, EventArgs.Empty);
        };
        UpdateFormat();
        UpdatePatternPreview();
        LoadAudioDevices();
    }

    /// <summary>The window has closed, saved or not: the shortcuts can be registered again.</summary>
    public event EventHandler? Closed;

    private static NSWindow CreateWindow()
    {
        var window = new NSWindow(new CGRect(0, 0, 680, 600), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
        {
            Title = "ShowTime — Réglages"
        };
        window.ReleaseWhenClosed(false);
        return window;
    }

    public void Show()
    {
        AppActivation.BringToFront();
        ShowWindow(this);
        Window!.Center();
        Window.MakeKeyAndOrderFront(this);
    }

    // ---- Layout ---------------------------------------------------------------------------------------------

    private NSStackView Build()
    {
        var hotkeyTitles = new[]
        {
            "Capturer une zone", "Capturer une fenêtre", "Capturer tout l'écran", "Capturer la dernière zone",
            "Vidéo d'une zone", "Vidéo de tout l'écran", "Démarrer l'enregistrement", "Arrêter l'enregistrement"
        };
        var save = NSButton.CreateButton("Enregistrer", Save);
        save.KeyEquivalent = "\r";
        var cancel = NSButton.CreateButton("Annuler", () => Window?.Close());
        cancel.KeyEquivalent = "\u001B";

        var rows = new List<NSView>
        {
            Section("Enregistrement"),
            Row("Dossier", _folder, NSButton.CreateButton("Parcourir…", BrowseFolder)),
            Row("Nom des fichiers", _pattern),
            Row("", _patternPreview),
            Row("Format", _format, NSTextField.CreateLabel("Qualité JPEG"), _quality, _qualityValue),
            Row("", _copyPath),
            Section("Après une capture"),
            Row("", _afterCapture),
            Section("Vidéo"),
            Row("ffmpeg", _ffmpeg, NSButton.CreateButton("Parcourir…", BrowseFfmpeg)),
            Row("", _ffmpegStatus),
            Row("Micro", _microphone),
            Row("", _recordMicrophone, _systemAudio),
            Row("", Help("Son de l'ordinateur : capté par ShowTime, sans pilote à installer. Les deux se changent aussi " +
                "sur la barre d'enregistrement : le dernier choix est retenu.")),
            Row("Images/seconde", _frameRate),
            Section("Sous-titres"),
            Row("", _transcribe),
            Row("", Help("La transcription se fait sur cet ordinateur, sans rien envoyer. Les sous-titres s'affichent à côté de " +
                "la vidéo pour être corrigés, et sont enregistrés dans un fichier .srt du même nom. Le choix se change aussi " +
                "sur la barre d'enregistrement.")),
            Row("Langue", _language),
            Row("Modèle", _model, NSButton.CreateButton("Parcourir…", BrowseModel)),
            Row("", _modelStatus),
            Section("Raccourcis clavier")
        };
        rows.AddRange(hotkeyTitles.Select((title, i) => Row(title, _hotkeys[i])));
        rows.Add(Row("Surligner (maintenir)", _highlightTrigger));
        rows.Add(Row("", _highlightWarning));
        rows.Add(Row("", _highlightStyleButton));
        rows.Add(Row("", NSButton.CreateButton("Raccourcis par défaut", DefaultHotkeys)));
        rows.Add(Row("", Help("Cliquez dans un champ puis appuyez sur la combinaison. ⌫ : aucun raccourci. " +
            "Démarrer, Arrêter et Surligner ne sont actifs que pendant une vidéo ; Surligner se maintient au clavier.")));
        rows.Add(Row("", _startup));
        var buttons = NSStackView.FromViews([cancel, save]);
        buttons.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        rows.Add(buttons);

        var form = NSStackView.FromViews([.. rows]);
        form.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        form.Alignment = NSLayoutAttribute.Leading;
        form.Spacing = 8;
        form.EdgeInsets = new NSEdgeInsets(16, 20, 16, 20);
        form.SetCustomSpacing(16, rows[^2]);
        foreach (var field in new[] { _folder, _pattern, _ffmpeg, _model })
            field.WidthAnchor.ConstraintGreaterThanOrEqualTo(330).Active = true;
        foreach (var field in _hotkeys.Append(_highlightTrigger))
            field.WidthAnchor.ConstraintEqualTo(200).Active = true;
        foreach (var help in new[] { _patternPreview, _ffmpegStatus, _modelStatus })
            help.PreferredMaxLayoutWidth = 460;
        return form;
    }

    /// <summary>The form at the top of a scroll view, as wide as the window.</summary>
    private static NSScrollView Scrollable(NSView form)
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        form.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(form);
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            DocumentView = document
        };
        var clip = scroll.ContentView;
        NSLayoutConstraint.ActivateConstraints(
        [
            form.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            form.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            form.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            form.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.TopAnchor.ConstraintEqualTo(clip.TopAnchor),
            document.LeadingAnchor.ConstraintEqualTo(clip.LeadingAnchor),
            document.WidthAnchor.ConstraintEqualTo(clip.WidthAnchor)
        ]);
        return scroll;
    }

    private static NSTextField Section(string title)
    {
        var label = NSTextField.CreateLabel(title);
        if (NSFont.BoldSystemFontOfSize(13) is { } font)
            label.Font = font;
        return label;
    }

    private static NSStackView Row(string label, params NSView[] views)
    {
        var title = NSTextField.CreateLabel(label);
        title.Alignment = NSTextAlignment.Right;
        title.WidthAnchor.ConstraintEqualTo(LabelWidth).Active = true;
        var row = NSStackView.FromViews([title, .. views]);
        row.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        row.Spacing = 8;
        return row;
    }

    private static NSTextField Help(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.TextColor = NSColor.SecondaryLabel;
        if (NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize) is { } font)
            label.Font = font;
        label.PreferredMaxLayoutWidth = 460;
        return label;
    }

    // ---- Fields ---------------------------------------------------------------------------------------------

    private string SelectedFormat() => _format.IndexOfSelectedItem == 1 ? "jpg" : "png";

    private void UpdateFormat()
    {
        _quality.Enabled = SelectedFormat() == "jpg";
        _qualityValue.IntValue = _quality.IntValue;
        UpdatePatternPreview();
    }

    private void UpdatePatternPreview()
    {
        var example = FileNamePattern.Expand(_pattern.StringValue.Trim(), _context.Settings.Counter, DateTime.Now);
        _patternPreview.StringValue = $"Exemple : {example}.{SelectedFormat()}   ·   Variables : %YYYY% %MM% %DD% %hh% %mm% %ss% %NUM%";
    }

    private string? UpdateFfmpegStatus()
    {
        var typed = _ffmpeg.StringValue.Trim();
        var path = _context.Locator.Find(typed);
        _ffmpegStatus.StringValue = path is not null
            ? (typed.Length == 0 ? "Détecté : " + path : "ffmpeg trouvé.")
            : "ffmpeg introuvable : les vidéos ne pourront pas être enregistrées. " + _context.Platform.InstallHint;
        _ffmpegStatus.TextColor = path is not null ? NSColor.SecondaryLabel : NSColor.SystemRed;
        return path;
    }

    private void UpdateModelStatus()
    {
        var typed = _model.StringValue.Trim();
        var path = _context.Models.Find(typed);
        _modelStatus.StringValue = path is not null
            ? (typed.Length == 0 ? "Livré avec ShowTime : " + path : "Modèle trouvé.")
            : "Modèle introuvable : les vidéos ne pourront pas être transcrites. Laissez le champ vide pour utiliser " +
              "celui livré avec ShowTime.";
        _modelStatus.TextColor = path is not null ? NSColor.SecondaryLabel : NSColor.SystemRed;
        _transcribe.Enabled = path is not null || _transcribe.State == NSCellStateValue.On;
    }

    /// <summary>Asks ffmpeg for the audio inputs; the computer sound is captured by ShowTime itself.</summary>
    private async void LoadAudioDevices()
    {
        // A newer load (ffmpeg chosen while it was still listing) wins; the older one must not fill the list too.
        var generation = ++_audioLoadGeneration;
        // Keep the choice being edited, not only the saved one.
        var selected = _microphone.IndexOfSelectedItem > 0 ? _microphone.TitleOfSelectedItem : _context.Settings.Microphone;

        var ffmpeg = UpdateFfmpegStatus();
        IReadOnlyList<string> devices = [];
        if (ffmpeg is not null)
            devices = await _context.AudioDevices.ListAsync(ffmpeg).ConfigureAwait(true);
        if (generation != _audioLoadGeneration)
            return;

        // A chosen microphone stays selectable even if it is unplugged right now.
        var microphones = AudioDevices.Microphones(devices, _context.Platform.SystemAudioDevice).ToList();
        if (!string.IsNullOrEmpty(selected) && !microphones.Contains(selected))
            microphones.Add(selected);
        _microphone.RemoveAllItems();
        _microphone.AddItems(["Premier micro détecté", .. microphones]);
        _microphone.SelectItem(string.IsNullOrEmpty(selected) ? 0 : microphones.IndexOf(selected) + 1);

        var hasSystemAudio = _context.Platform.CanRecordSystemAudio(devices);
        _systemAudio.Enabled = hasSystemAudio || _systemAudio.State == NSCellStateValue.On;
        _systemAudio.ToolTip = hasSystemAudio
            ? "Capté par ShowTime, avec " + _context.Platform.SystemAudioRequirement + "."
            : "Nécessite " + _context.Platform.SystemAudioRequirement + " : introuvable sur ce Mac.";
    }

    private void BrowseFolder()
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = true;
        panel.CanChooseFiles = false;
        panel.CanCreateDirectories = true;
        panel.Prompt = "Choisir";
        if (Directory.Exists(_folder.StringValue))
            panel.DirectoryUrl = NSUrl.FromFilename(_folder.StringValue);
        if (panel.RunModal() == 1 && panel.Url?.Path is { } path)
            _folder.StringValue = path;
    }

    private void BrowseFfmpeg()
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = false;
        panel.CanChooseFiles = true;
        panel.Prompt = "Choisir";
        if (panel.RunModal() != 1 || panel.Url?.Path is not { } path)
            return;
        _ffmpeg.StringValue = path;
        LoadAudioDevices();
    }

    private void BrowseModel()
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = false;
        panel.CanChooseFiles = true;
        panel.Prompt = "Choisir";
        if (panel.RunModal() == 1 && panel.Url?.Path is { } path)
        {
            _model.StringValue = path;
            UpdateModelStatus();
        }
    }

    private void DefaultHotkeys()
    {
        var d = _context.Defaults;
        string[] defaults =
        [
            d.HotkeyRegion, d.HotkeyWindow, d.HotkeyFullScreen, d.HotkeyLastRegion,
            d.HotkeyVideoRegion, d.HotkeyVideoFullScreen, d.HotkeyVideoStart, d.HotkeyVideoStop
        ];
        for (var i = 0; i < _hotkeys.Length; i++)
            _hotkeys[i].Shortcut = defaults[i];
        _highlightTrigger.Shortcut = d.HighlightTrigger;
        UpdateHighlightWarning();
    }

    private void UpdateHighlightStyleButton() => _highlightStyleButton.Enabled = !_context.Recording.IsBusy;

    private void Recording_StateChanged(object? sender, EventArgs e) => _context.UiThread.Post(UpdateHighlightStyleButton);

    private void EditHighlightStyle()
    {
        if (_context.Recording.IsBusy)
            return;
        var editor = new HighlightStyleWindowController(_highlightStyle, _context.Defaults, _context.Overlays, _context.UiThread, _context.Clock,
            _context.Recording);
        Window!.BeginSheet(editor.Window!, _ =>
        {
            if (editor.Accepted)
                _highlightStyle = editor.Chosen;
            // The sheet ends from its own button's action: release it once that action has returned.
            _context.UiThread.Post(() =>
            {
                editor.Close();
                editor.Dispose();
            });
        });
    }

    private void UpdateHighlightWarning() =>
        _highlightWarning.Hidden = !(HighlightTrigger.TryParse(_highlightTrigger.Shortcut, out var trigger) && trigger.BlocksTyping);

    private void Save()
    {
        var h = _hotkeys.Select(f => f.Shortcut).ToArray();
        var form = new SettingsForm(
            _folder.StringValue, _pattern.StringValue, SelectedFormat(), (int)_quality.IntValue, _copyPath.State == NSCellStateValue.On,
            AfterCaptureChoices[(int)Math.Max(0, _afterCapture.IndexOfSelectedItem)].Action,
            h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7],
            _highlightTrigger.Shortcut, _ffmpeg.StringValue,
            _microphone.IndexOfSelectedItem > 0 ? _microphone.TitleOfSelectedItem ?? "" : "",
            _recordMicrophone.State == NSCellStateValue.On, _systemAudio.State == NSCellStateValue.On,
            SettingsForm.FrameRates[(int)Math.Max(0, _frameRate.IndexOfSelectedItem)],
            _transcribe.State == NSCellStateValue.On,
            SettingsForm.TranscriptionLanguages[(int)Math.Max(0, _language.IndexOfSelectedItem)].Code, _model.StringValue,
            _highlightStyle);
        if (_context.Editor.TryApply(form) is { } problem)
        {
            MacDialogs.Show(problem, NSAlertStyle.Warning);
            return;
        }

        try
        {
            _context.Startup.SetEnabled(_startup.State == NSCellStateValue.On);
        }
        catch (InvalidOperationException ex)
        {
            MacDialogs.Show(ex.Message + "\n\nLes autres réglages sont enregistrés.", NSAlertStyle.Warning);
        }
        Window?.Close();
    }

    /// <summary>The scroll view's document, laid out from the top.</summary>
    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
