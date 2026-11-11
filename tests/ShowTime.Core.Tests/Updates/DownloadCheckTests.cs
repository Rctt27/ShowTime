using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using ShowTime.Core.Updates;

namespace ShowTime.Core.Tests.Updates;

public sealed class DownloadCheckTests
{
    private static readonly byte[] Content = "ShowTime 0.6.3"u8.ToArray();
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Content));

    private readonly MockFileSystem _fs = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), "ShowTime.zip");

    [Fact]
    public void A_file_of_the_announced_size_and_digest_matches()
    {
        _fs.AddFile(_file, new MockFileData(Content));

        DownloadCheck.Matches(_fs, _file, Content.Length, Digest).ShouldBeTrue();
    }

    [Fact]
    public void One_byte_changed_does_not_match()
    {
        var altered = Content.ToArray();
        altered[3] ^= 1;
        _fs.AddFile(_file, new MockFileData(altered));

        DownloadCheck.Matches(_fs, _file, Content.Length, Digest).ShouldBeFalse();
    }

    [Fact]
    public void A_file_of_another_size_does_not_match()
    {
        _fs.AddFile(_file, new MockFileData(Content));

        DownloadCheck.Matches(_fs, _file, Content.Length + 1, Digest).ShouldBeFalse();
    }

    [Fact]
    public void The_digest_is_compared_without_regard_to_case()
    {
        _fs.AddFile(_file, new MockFileData(Content));

        DownloadCheck.Matches(_fs, _file, Content.Length, Digest.ToUpperInvariant()).ShouldBeTrue();
    }

    [Fact]
    public void A_missing_file_does_not_match()
    {
        DownloadCheck.Matches(_fs, _file, Content.Length, Digest).ShouldBeFalse();
    }
}
