using System.Net;
using ShowTime.Core.Updates;
using ShowTime.Infrastructure.Updates;

namespace ShowTime.Infrastructure.IntegrationTests.Updates;

public sealed class GitHubReleaseFeedTests
{
    [Fact]
    public async Task The_feed_asks_GitHub_for_the_latest_release()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v1.0.0\"}") }));
        using var feed = new GitHubReleaseFeed(handler, new SemanticVersion(0, 6, 3));

        var body = await feed.GetLatestAsync(TestContext.Current.CancellationToken);

        body.ShouldBe("{\"tag_name\":\"v1.0.0\"}");
        handler.Request!.RequestUri.ShouldBe(GitHubRelease.LatestUrl);
        handler.Request.Headers.UserAgent.ToString().ShouldBe("ShowTime/0.6.3");
        handler.Request.Headers.Accept.ToString().ShouldBe("application/vnd.github+json");
    }

    [Fact]
    public async Task The_feed_throws_on_403()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var feed = new GitHubReleaseFeed(handler, new SemanticVersion(0, 6, 3));

        await Should.ThrowAsync<HttpRequestException>(() => feed.GetLatestAsync(TestContext.Current.CancellationToken));
    }
}
