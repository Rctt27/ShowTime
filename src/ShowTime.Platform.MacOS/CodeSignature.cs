namespace ShowTime.Platform.MacOS;

/// <summary>
/// Reads what <c>codesign</c> prints about an app, so that an update is only installed when it is signed like the app
/// it replaces: macOS keeps the Screen Recording and Microphone permissions only for the same signature.
/// </summary>
public static class CodeSignature
{
    private const string DesignatedPrefix = "designated => ";

    /// <summary>Whether <c>codesign -dv</c>'s output describes an ad hoc signature (a build without the certificate).</summary>
    public static bool IsAdHoc(string codesignDisplayOutput)
    {
        ArgumentNullException.ThrowIfNull(codesignDisplayOutput);
        return Lines(codesignDisplayOutput).Any(line => line == "Signature=adhoc");
    }

    /// <summary>The designated requirement in <c>codesign -d -r-</c>'s output, or null when it has none.</summary>
    public static string? DesignatedRequirement(string codesignRequirementOutput)
    {
        ArgumentNullException.ThrowIfNull(codesignRequirementOutput);
        return Lines(codesignRequirementOutput)
            .Where(line => line.StartsWith(DesignatedPrefix, StringComparison.Ordinal))
            .Select(line => line[DesignatedPrefix.Length..].Trim())
            .FirstOrDefault(requirement => requirement.Length > 0);
    }

    // codesign prefixes with "# " a designated requirement it derived rather than read from the signature.
    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(line => line.Trim().TrimStart('#').Trim());
}
