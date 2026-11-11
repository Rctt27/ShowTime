using System.Globalization;

namespace ShowTime.Core.Updates;

/// <summary>A <c>MAJOR.MINOR.PATCH</c> version; anything with a suffix (<c>-beta</c>) is not one.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    /// <summary>Reads <c>0.6.3</c> or <c>v0.6.3</c>.</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var parts = (text.StartsWith('v') ? text[1..] : text).Split('.');
        if (parts.Length != 3
            || !TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    /// <summary>The assembly version (<c>0.6.3.0</c>) without its revision.</summary>
    public static SemanticVersion From(Version assemblyVersion) =>
        new(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0));

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private static bool TryNumber(string text, out int number)
    {
        number = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
