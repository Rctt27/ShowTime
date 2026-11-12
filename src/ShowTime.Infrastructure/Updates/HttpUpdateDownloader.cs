using ShowTime.Core.Ports;

namespace ShowTime.Infrastructure.Updates;

/// <summary>Downloads a release asset to a file, reporting progress from the Content-Length.</summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader, IDisposable
{
    private const int BufferSize = 81920;

    private readonly HttpClient _client;
    private readonly TimeSpan _idleTimeout;

    /// <param name="idleTimeout">
    /// How long without a byte before giving up, 30 s by default: a 200 MB download has no overall timeout, but a laptop
    /// put to sleep or a Wi-Fi network left would otherwise leave it waiting for ever.
    /// </param>
    public HttpUpdateDownloader(HttpMessageHandler handler, TimeSpan? idleTimeout = null)
    {
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task DownloadAsync(Uri url, string destinationFile, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            await using (var target = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
                {
                    idle.CancelAfter(_idleTimeout);
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
        catch (OperationCanceledException ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            DeleteQuietly(destinationFile);
            throw new UpdateException("le téléchargement s'est interrompu", ex);
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
