using System.IO.Abstractions;
using System.Security.Cryptography;

namespace ShowTime.Core.Updates;

/// <summary>Whether a downloaded release is the one GitHub announced.</summary>
public static class DownloadCheck
{
    /// <summary>True when <paramref name="file"/> has the announced size and SHA-256 digest (hex, in either case).</summary>
    public static bool Matches(IFileSystem fileSystem, string file, long size, string sha256)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var info = fileSystem.FileInfo.New(file);
        if (!info.Exists || info.Length != size)
            return false;

        using var stream = fileSystem.File.OpenRead(file);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(digest, sha256, StringComparison.OrdinalIgnoreCase);
    }
}
