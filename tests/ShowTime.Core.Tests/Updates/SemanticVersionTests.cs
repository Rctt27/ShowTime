using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.6.3")]
    [InlineData("v0.6.3")]
    public void TryParse_reads_plain_and_v_prefixed_versions(string text)
    {
        SemanticVersion.TryParse(text, out var version).ShouldBeTrue();
        version.ShouldBe(new SemanticVersion(0, 6, 3));
    }

    [Theory]
    [InlineData("0.7.0-beta")]
    [InlineData("1.2")]
    [InlineData("v")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    public void TryParse_rejects(string? text) =>
        SemanticVersion.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Versions_compare_numerically()
    {
        (new SemanticVersion(0, 10, 0) > new SemanticVersion(0, 9, 9)).ShouldBeTrue();
        (new SemanticVersion(1, 0, 0) > new SemanticVersion(0, 99, 99)).ShouldBeTrue();
        (new SemanticVersion(0, 9, 9) < new SemanticVersion(0, 10, 0)).ShouldBeTrue();
        (new SemanticVersion(0, 6, 3) >= new SemanticVersion(0, 6, 3)).ShouldBeTrue();
        (new SemanticVersion(0, 6, 3) <= new SemanticVersion(0, 6, 3)).ShouldBeTrue();
        (new SemanticVersion(0, 6, 2) >= new SemanticVersion(0, 6, 3)).ShouldBeFalse();
        new SemanticVersion(0, 6, 3).ShouldBe(new SemanticVersion(0, 6, 3));
    }

    [Fact]
    public void From_drops_the_revision() =>
        SemanticVersion.From(new Version(0, 6, 3, 0)).ToString().ShouldBe("0.6.3");
}
