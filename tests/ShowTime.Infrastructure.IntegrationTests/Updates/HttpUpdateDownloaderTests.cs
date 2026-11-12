using System.Net;
using ShowTime.Core.Ports;
using ShowTime.Infrastructure.Updates;

namespace ShowTime.Infrastructure.IntegrationTests.Updates;

public sealed class HttpUpdateDownloaderTests : IDisposable
{
    private static readonly Uri Url = new("https://example.invalid/ShowTime.zip");
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task The_download_writes_the_file_and_reports_progress()
    {
        var bytes = new byte[1024 * 1024];
        new Random(1).NextBytes(bytes);
        var handler = new StubHandler((_, _) => Task.FromResult(Response(new ChunkedStream(bytes, 64 * 1024, failAfter: null), bytes.Length)));
        using var downloader = new HttpUpdateDownloader(handler);
        var progress = new RecordingProgress();
        var file = _folder.File("a.zip");

        await downloader.DownloadAsync(Url, file, progress, TestContext.Current.CancellationToken);

        (await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)).ShouldBe(bytes);
        progress.Values.Last().ShouldBe(1.0);
        progress.Values.ShouldBe(progress.Values.Order().ToList());
        progress.Values.Count.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task A_cancelled_download_leaves_no_file()
    {
        using var cts = new CancellationTokenSource();
        var bytes = new byte[256 * 1024];
        var handler = new StubHandler((_, _) => Task.FromResult(Response(new ChunkedStream(bytes, 64 * 1024, failAfter: null), bytes.Length)));
        using var downloader = new HttpUpdateDownloader(handler);
        var file = _folder.File("b.zip");
        var progress = new CancelOnFirst(cts);

        await Should.ThrowAsync<OperationCanceledException>(() => downloader.DownloadAsync(Url, file, progress, cts.Token));

        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task A_download_that_stops_receiving_fails_instead_of_waiting_for_ever()
    {
        // A laptop put to sleep, or a Wi-Fi network left, mid-download: no error, no more bytes either.
        var handler = new StubHandler((_, _) => Task.FromResult(Response(new StalledStream(), 1024 * 1024)));
        using var downloader = new HttpUpdateDownloader(handler, idleTimeout: TimeSpan.FromMilliseconds(200));
        var file = _folder.File("stalled.zip");

        var failure = await Should.ThrowAsync<UpdateException>(
            () => downloader.DownloadAsync(Url, file, new RecordingProgress(), TestContext.Current.CancellationToken));

        failure.Message.ShouldBe("le téléchargement s'est interrompu");
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_download_leaves_no_file()
    {
        var bytes = new byte[256 * 1024];
        var handler = new StubHandler((_, _) => Task.FromResult(Response(new ChunkedStream(bytes, 64 * 1024, failAfter: 2), bytes.Length)));
        using var downloader = new HttpUpdateDownloader(handler);
        var file = _folder.File("c.zip");

        await Should.ThrowAsync<IOException>(() => downloader.DownloadAsync(Url, file, new RecordingProgress(), TestContext.Current.CancellationToken));

        File.Exists(file).ShouldBeFalse();
    }

    private static HttpResponseMessage Response(Stream body, long length)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class CancelOnFirst(CancellationTokenSource source) : IProgress<double>
    {
        public void Report(double value) => source.Cancel();
    }

    /// <summary>Hands out at most one chunk per read, and fails after a number of chunks when asked.</summary>
    /// <summary>A response body whose bytes never come.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class ChunkedStream(byte[] data, int chunk, int? failAfter) : Stream
    {
        private int _position;
        private int _chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (failAfter is { } limit && _chunks >= limit)
            {
                throw new IOException("connection lost");
            }

            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            _chunks++;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
