// Pure text: it runs on every OS, although the assembly is marked for macOS.
#pragma warning disable CA1416

using ShowTime.Platform.MacOS;

namespace ShowTime.Platform.Tests.MacOS;

/// <summary>Reading what <c>codesign</c> prints; pure text, so it runs on every OS.</summary>
public sealed class CodeSignatureTests
{
    // codesign -dv --verbose=2 of a release signed with the LocalTake certificate (written on stderr).
    private const string SignedDisplay = """
        Executable=/Applications/ShowTime.app/Contents/MacOS/ShowTime
        Identifier=io.github.rctt27.showtime
        Format=app bundle with Mach-O universal (x86_64 arm64)
        CodeDirectory v=20500 size=2047 flags=0x10000(runtime) hashes=53+7 location=embedded
        Signature size=1712
        Authority=LocalTake
        Signed Time=11 Nov 2026 at 21:12:04
        Info.plist entries=24
        TeamIdentifier=not set
        Runtime Version=26.5.0
        Sealed Resources version=2 rules=13 files=412
        Internal requirements count=1 size=112
        """;

    // The same, for a build signed ad hoc (no signing secrets).
    private const string AdHocDisplay = """
        Executable=/Applications/ShowTime.app/Contents/MacOS/ShowTime
        Identifier=io.github.rctt27.showtime
        Format=app bundle with Mach-O universal (x86_64 arm64)
        CodeDirectory v=20400 size=2047 flags=0x2(adhoc) hashes=53+7 location=embedded
        Signature=adhoc
        Info.plist entries=24
        TeamIdentifier=not set
        Sealed Resources version=2 rules=13 files=412
        Internal requirements count=0 size=12
        """;

    // codesign -d -r- of the signed release.
    private const string SignedRequirement = """
        Executable=/Applications/ShowTime.app/Contents/MacOS/ShowTime
        designated => identifier "io.github.rctt27.showtime" and certificate leaf = H"6f1c0a3e9b2d4c5f7a8e1d0b3c2a4f5e6d7c8b9a"
        """;

    [Fact]
    public void A_signed_app_is_not_ad_hoc() => CodeSignature.IsAdHoc(SignedDisplay).ShouldBeFalse();

    [Fact]
    public void An_ad_hoc_app_is_recognised() => CodeSignature.IsAdHoc(AdHocDisplay).ShouldBeTrue();

    [Fact]
    public void The_designated_requirement_names_the_identifier_and_the_certificate()
    {
        CodeSignature.DesignatedRequirement(SignedRequirement)
            .ShouldBe("identifier \"io.github.rctt27.showtime\" and certificate leaf = H\"6f1c0a3e9b2d4c5f7a8e1d0b3c2a4f5e6d7c8b9a\"");
    }

    [Fact]
    public void Without_a_designated_line_there_is_no_requirement() =>
        CodeSignature.DesignatedRequirement("Executable=/Applications/ShowTime.app/Contents/MacOS/ShowTime\n").ShouldBeNull();
}
