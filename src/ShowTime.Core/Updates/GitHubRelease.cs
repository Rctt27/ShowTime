using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShowTime.Core.Updates;

/// <summary>The platform whose release asset is wanted.</summary>
public enum UpdatePlatform
{
    WindowsX64,
    MacOS,
}

/// <summary>A release that can be installed. <see cref="Sha256"/> is lowercase hex, without the <c>sha256:</c> prefix.</summary>
public sealed record ReleaseInfo(SemanticVersion Version, Uri DownloadUrl, long Size, string Sha256, Uri PageUrl);

/// <summary>Reads GitHub's "latest release" answer.</summary>
public static class GitHubRelease
{
    private const string DigestPrefix = "sha256:";

    public static readonly Uri LatestUrl = new("https://api.github.com/repos/Rctt27/ShowTime/releases/latest");

    /// <summary>The asset name the release workflow gives to a version.</summary>
    public static string AssetName(SemanticVersion version, UpdatePlatform platform) => platform switch
    {
        UpdatePlatform.WindowsX64 => $"ShowTime-{version}-windows-x64.zip",
        UpdatePlatform.MacOS => $"ShowTime-{version}-macos.zip",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>
    /// The release for a platform, or null when the release cannot be installed: a draft, a pre-release, a tag that is
    /// not <c>vX.Y.Z</c>, or no asset with a SHA-256 digest for the platform.
    /// </summary>
    /// <exception cref="JsonException">The answer is not a release: invalid JSON, or no <c>tag_name</c>.</exception>
    public static ReleaseInfo? Parse(string json, UpdatePlatform platform)
    {
        var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubReleaseDto);
        if (release?.TagName is null)
            throw new JsonException("The answer is not a GitHub release: it has no tag_name.");

        if (release.Draft || release.Prerelease
            || !SemanticVersion.TryParse(release.TagName, out var version)
            || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var pageUrl))
        {
            return null;
        }

        var name = AssetName(version, platform);
        var asset = release.Assets?.FirstOrDefault(a => a.Name == name);
        if (asset?.Digest is not { } digest
            || !digest.StartsWith(DigestPrefix, StringComparison.Ordinal)
            || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl))
        {
            return null;
        }

        return new ReleaseInfo(version, downloadUrl, asset.Size, digest[DigestPrefix.Length..].ToLowerInvariant(), pageUrl);
    }
}

internal sealed record GitHubReleaseDto(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAssetDto>? Assets);

internal sealed record GitHubAssetDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
