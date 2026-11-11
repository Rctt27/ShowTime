using System.Text.Json.Nodes;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class GitHubReleaseTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "release-v0.6.3.json"));

    private static string Modified(Action<JsonObject> change)
    {
        var json = JsonNode.Parse(Fixture())!.AsObject();
        change(json);
        return json.ToJsonString();
    }

    [Fact]
    public void AssetName_matches_the_release_workflow()
    {
        var version = new SemanticVersion(0, 6, 3);
        GitHubRelease.AssetName(version, UpdatePlatform.WindowsX64).ShouldBe("ShowTime-0.6.3-windows-x64.zip");
        GitHubRelease.AssetName(version, UpdatePlatform.MacOS).ShouldBe("ShowTime-0.6.3-macos.zip");
    }

    [Fact]
    public void LatestUrl_points_at_the_latest_release() =>
        GitHubRelease.LatestUrl.ToString().ShouldBe("https://api.github.com/repos/Rctt27/ShowTime/releases/latest");

    [Theory]
    [InlineData(UpdatePlatform.WindowsX64, "ShowTime-0.6.3-windows-x64.zip", 225831069L,
        "fc41c072200d9f9354ce537000a618e150a83c906bc885d070bd677933cd9605")]
    [InlineData(UpdatePlatform.MacOS, "ShowTime-0.6.3-macos.zip", 266764067L,
        "0219c5f6fb1c8f48cef5352effaefd700a10c8270f0f5e9160c4dd06e8019a1e")]
    public void Parse_reads_the_v0_6_3_release(UpdatePlatform platform, string asset, long size, string sha256)
    {
        var info = GitHubRelease.Parse(Fixture(), platform);

        info.ShouldNotBeNull();
        info.Version.ShouldBe(new SemanticVersion(0, 6, 3));
        info.DownloadUrl.ToString().ShouldBe("https://github.com/Rctt27/ShowTime/releases/download/v0.6.3/" + asset);
        info.Size.ShouldBe(size);
        info.Sha256.ShouldBe(sha256);
        info.PageUrl.ToString().ShouldBe("https://github.com/Rctt27/ShowTime/releases/tag/v0.6.3");
    }

    public static TheoryData<string> Unusable() => new()
    {
        "{ not json",
        "[]",
        Modified(j => j["prerelease"] = true),
        Modified(j => j["draft"] = true),
        Modified(j => j["tag_name"] = "v0.7.0-rc1"),
        Modified(j => j["assets"]!.AsArray().Clear()),
        Modified(j => j["assets"]![1]!.AsObject().Remove("digest")),
        Modified(j => j["assets"]![1]!.AsObject()["digest"] = "sha1:abc"),
        Modified(j => j["assets"]!.AsArray().RemoveAt(1)),
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void Parse_returns_null_when_the_release_cannot_be_installed(string json) =>
        GitHubRelease.Parse(json, UpdatePlatform.WindowsX64).ShouldBeNull();
}
