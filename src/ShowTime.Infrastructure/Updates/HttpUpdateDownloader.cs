using ShowTime.Core.Ports;

namespace ShowTime.Infrastructure.Updates;

/// <summary>Downloads a release asset to a file, reporting progress from the Content-Length.</summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader, IDisposable
{
    private const int BufferSize = 81920;

    private readonly HttpClient _client;

    public HttpUpdateDownloader(HttpMessageHandler handler) =>
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task DownloadAsync(Uri url, string destinationFile, IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total is > 0)
                    {
                        progress.Report(Math.Min(1.0, (double)done / total.Value));
                    }
                }
            }

            progress.Report(1.0);
        }
        catch
        {
            DeleteQuietly(destinationFile);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Nothing more to do: the caller sees the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
