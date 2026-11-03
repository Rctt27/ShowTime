using System.Globalization;
using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Tests.Recording;

public sealed class RecordingCommandTests
{
    public sealed record ArgumentsCase(
        string Name, int X, int Y, int Width, int Height, int FrameRate, string? Microphone, bool SystemAudio,
        string Culture, string[] Arguments);

    // Deliberate change: the cases with the computer sound recorded a virtual audio driver through dshow. ShowTime now
    // captures it itself and pipes it to ffmpeg after the screen (DirectShowGdigrabTests); every other case is v0.1.0's.
    public static TheoryData<string> LegacyCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixtures.Load<List<ArgumentsCase>>("ffmpeg-arguments.json").Where(c => !c.SystemAudio))
            data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(LegacyCaseNames))]
    public void Windows_command_is_the_one_of_v0_1_0(string name)
    {
        var c = Fixtures.Load<List<ArgumentsCase>>("ffmpeg-arguments.json").Single(x => x.Name == name);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(c.Culture);
            var inputs = new DirectShowGdigrab(() => true).BuildInputs(
                new PixelRect(c.X, c.Y, c.Width, c.Height), new VideoOptions(c.FrameRate, c.Microphone, c.SystemAudio), null);

            RecordingCommand.Build(inputs, @"C:\Out\video.mp4").ShouldBe(c.Arguments);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Trailing_audio_inputs_are_opened_after_the_screen()
    {
        var inputs = new CaptureInputs([["-i", "mic"]], ["-i", "screen"], TrailingAudioInputs: [["-i", "late"]], DownmixToStereo: true);
        RecordingCommand.Build(inputs, "out.mp4").ShouldBe(
        [
            "-hide_banner", "-loglevel", "level+info", "-nostats", "-y",
            "-i", "mic", "-i", "screen", "-i", "late",
            "-filter_complex", "[0:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0,volume=2[a]",
            "-map", "1:v", "-map", "[a]",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-ac", "2",
            "-movflags", "+faststart", "out.mp4"
        ]);
    }

    [Fact]
    public void A_single_trailing_input_is_mapped_by_its_index()
    {
        var args = RecordingCommand.Build(new CaptureInputs([], ["-i", "screen"], TrailingAudioInputs: [["-i", "late"]]), "out.mp4").ToList();
        args.ShouldContain("-map");
        args.Skip(args.IndexOf("-map")).Take(4).ShouldBe(["-map", "0:v", "-map", "1:a"]);
        args.ShouldNotContain("-ac");
    }

    [Fact]
    public void A_microphone_track_is_a_second_output_with_the_microphone_alone_as_16_kHz_mono_wav()
    {
        // With the computer sound too, the video mixes both; the track keeps only the microphone, the first input.
        var inputs = new CaptureInputs([["-i", "mic"]], ["-i", "screen"], TrailingAudioInputs: [["-i", "late"]], DownmixToStereo: true);

        RecordingCommand.Build(inputs, "out.mp4", "mic.wav").ShouldBe(
        [
            .. RecordingCommand.Build(inputs, "out.mp4"),
            "-map", "0:a", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "mic.wav"
        ]);
    }

    [Fact]
    public void A_trailing_microphone_is_the_track_and_the_first_in_the_mix()
    {
        // macOS: the microphone and the Mac's sound are both pipes opened after the screen, the microphone first.
        var inputs = new CaptureInputs([], ["-i", "screen"], TrailingAudioInputs: [["-i", "mic"], ["-i", "late"]], MicrophoneTrails: true);

        var args = RecordingCommand.Build(inputs, "out.mp4", "mic.wav").ToList();

        args[args.IndexOf("-filter_complex") + 1].ShouldStartWith("[1:a][2:a]amix");
        args.TakeLast(9).ShouldBe(["-map", "1:a", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "mic.wav"]);
    }

    [Fact]
    public void A_microphone_track_needs_a_microphone()
    {
        Should.Throw<ArgumentException>(() =>
            RecordingCommand.Build(new CaptureInputs([], ["-i", "screen"], TrailingAudioInputs: [["-i", "late"]]), "out.mp4", "mic.wav"));
    }

    [Fact]
    public void A_video_filter_is_applied_to_the_screen_stream()
    {
        var inputs = new CaptureInputs([], ["-f", "lavfi", "-i", "testsrc"], VideoFilter: "crop=10:10:0:0");
        var args = RecordingCommand.Build(inputs, "out.mp4");
        args.ShouldContain("-vf");
        args[args.ToList().IndexOf("-vf") + 1].ShouldBe("crop=10:10:0:0");
    }
}
