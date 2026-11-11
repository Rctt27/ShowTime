namespace ShowTime.Core.Updates;

/// <summary>The update item at the top of the tray or menu bar menu, for both apps.</summary>
public sealed record UpdateMenuEntry(bool Visible, string Text, bool Enabled)
{
    private static readonly UpdateMenuEntry Hidden = new(false, "", false);

    /// <param name="automatic">Whether ShowTime checks by itself (<c>CheckForUpdates</c>); without it, the menu offers a check.</param>
    public static UpdateMenuEntry For(UpdateState state, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = state.Release?.Version.ToString();
        return state.Stage switch
        {
            UpdateStage.Idle => automatic ? Hidden : new(true, "Rechercher une mise à jour", true),
            UpdateStage.Checking => automatic ? Hidden : new(true, "Recherche d'une mise à jour…", false),
            UpdateStage.Available => new(true, $"Installer la version {version}", true),
            UpdateStage.Downloading => new(true, $"Téléchargement de la version {version}… {Percent(state.Progress)} %", false),
            UpdateStage.WaitingForRecording => new(true, "Installation après l'enregistrement…", false),
            UpdateStage.Downloaded => new(true, $"Installer la version {version} (téléchargée)", true),
            UpdateStage.Installing => new(true, $"Installation de la version {version}…", false),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    /// <summary>The icon's tooltip once a version is offered, until it is installed; null otherwise.</summary>
    public static string? ToolTip(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Stage is UpdateStage.Idle or UpdateStage.Checking || state.Release is not { } release
            ? null
            : $"ShowTime — version {release.Version} disponible";
    }

    /// <summary>Whole percents, rounded down so that 100 % only shows once the download is complete.</summary>
    private static int Percent(double progress) => Math.Clamp((int)Math.Floor((progress * 100) + 1e-9), 0, 100);
}
