using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

/// <summary>The tray or menu bar item for each stage of an update.</summary>
public sealed class UpdateMenuEntryTests
{
    private static readonly ReleaseInfo Release = new(
        new SemanticVersion(0, 6, 3),
        new Uri("https://github.com/Rctt27/ShowTime/releases/download/v0.6.3/ShowTime-0.6.3-windows-x64.zip"),
        225831069,
        "fc41c072200d9f9354ce537000a618e150a83c906bc885d070bd677933cd9605",
        new Uri("https://github.com/Rctt27/ShowTime/releases/tag/v0.6.3"));

    private static UpdateState At(UpdateStage stage, double progress = 0) =>
        new(stage, stage is UpdateStage.Idle or UpdateStage.Checking ? null : Release, progress);

    [Theory]
    [InlineData(UpdateStage.Idle)]
    [InlineData(UpdateStage.Checking)]
    public void Nothing_shows_while_checking_automatically(UpdateStage stage) =>
        UpdateMenuEntry.For(At(stage), automatic: true).Visible.ShouldBeFalse();

    [Fact]
    public void Without_automatic_checks_the_menu_offers_one() =>
        UpdateMenuEntry.For(At(UpdateStage.Idle), automatic: false)
            .ShouldBe(new UpdateMenuEntry(true, "Rechercher une mise à jour", true));

    [Fact]
    public void A_manual_check_under_way_is_greyed() =>
        UpdateMenuEntry.For(At(UpdateStage.Checking), automatic: false)
            .ShouldBe(new UpdateMenuEntry(true, "Recherche d'une mise à jour…", false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_available_version_is_offered(bool automatic) =>
        UpdateMenuEntry.For(At(UpdateStage.Available), automatic)
            .ShouldBe(new UpdateMenuEntry(true, "Installer la version 0.6.3", true));

    [Fact]
    public void A_download_shows_its_progress() =>
        UpdateMenuEntry.For(At(UpdateStage.Downloading, 0.42), automatic: true)
            .ShouldBe(new UpdateMenuEntry(true, "Téléchargement de la version 0.6.3… 42 %", false));

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.29, "29")]
    [InlineData(0.999, "99")]
    [InlineData(1.0, "100")]
    public void The_progress_is_in_whole_percents(double progress, string percent) =>
        UpdateMenuEntry.For(At(UpdateStage.Downloading, progress), automatic: true).Text
            .ShouldBe($"Téléchargement de la version 0.6.3… {percent} %");

    [Fact]
    public void The_install_waits_for_the_recording() =>
        UpdateMenuEntry.For(At(UpdateStage.WaitingForRecording), automatic: true)
            .ShouldBe(new UpdateMenuEntry(true, "Installation après l'enregistrement…", false));

    [Fact]
    public void A_downloaded_version_is_offered_again() =>
        UpdateMenuEntry.For(At(UpdateStage.Downloaded), automatic: true)
            .ShouldBe(new UpdateMenuEntry(true, "Installer la version 0.6.3 (téléchargée)", true));

    [Fact]
    public void Installing_is_greyed() =>
        UpdateMenuEntry.For(At(UpdateStage.Installing), automatic: false)
            .ShouldBe(new UpdateMenuEntry(true, "Installation de la version 0.6.3…", false));

    [Theory]
    [InlineData(UpdateStage.Idle)]
    [InlineData(UpdateStage.Checking)]
    public void No_tooltip_without_a_version(UpdateStage stage) =>
        UpdateMenuEntry.ToolTip(At(stage)).ShouldBeNull();

    [Theory]
    [InlineData(UpdateStage.Available)]
    [InlineData(UpdateStage.Downloading)]
    [InlineData(UpdateStage.WaitingForRecording)]
    [InlineData(UpdateStage.Downloaded)]
    [InlineData(UpdateStage.Installing)]
    public void The_tooltip_names_the_version(UpdateStage stage) =>
        UpdateMenuEntry.ToolTip(At(stage)).ShouldBe("ShowTime — version 0.6.3 disponible");
}
