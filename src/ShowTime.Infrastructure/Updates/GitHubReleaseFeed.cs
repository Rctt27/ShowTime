using System.Net.Http.Headers;
using ShowTime.Core.Ports;
using ShowTime.Core.Updates;

namespace ShowTime.Infrastructure.Updates;

/// <summary>Asks GitHub for the latest release, unauthenticated.</summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    private readonly HttpClient _client;

    public GitHubReleaseFeed(HttpMessageHandler handler, SemanticVersion current)
    {
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShowTime", current.ToString()));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(GitHubRelease.LatestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
