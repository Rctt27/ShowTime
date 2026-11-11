namespace ShowTime.Infrastructure.IntegrationTests.Updates;

/// <summary>An <see cref="HttpMessageHandler"/> answering from a delegate; no network.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        return respond(request, cancellationToken);
    }
}
