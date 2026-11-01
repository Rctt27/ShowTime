using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Tests.Recording;

/// <summary>The computer sound, read from ShowTime's pipe instead of a virtual audio driver.</summary>
public sealed class DirectShowGdigrabTests
{
    private static readonly PixelRect Area = new(100, 200, 640, 480);
    private static readonly PipedAudio Pipe = new(@"\\.\pipe\lt", new RawAudioFormat("f32le", 48000, 2));

    private static IReadOnlyList<string> Command(VideoOptions options, PipedAudio? pipe) =>
        RecordingCommand.Build(new DirectShowGdigrab(() => true).BuildInputs(Area, options, pipe), "out.mp4");

    private static int IndexOf(IReadOnlyList<string> args, params string[] sequence) =>
        Enumerable.Range(0, args.Count - sequence.Length + 1)
            .FirstOrDefault(i => args.Skip(i).Take(sequence.Length).SequenceEqual(sequence), -1);

    [Fact]
    public void The_install_hint_says_ffmpeg_comes_with_ShowTime()
    {
        var hint = new DirectShowGdigrab(() => true).InstallHint;
        hint.ShouldContain("livré avec ShowTime");
        hint.ShouldContain("ffmpeg.exe");
        // The zip keeps ffmpeg.exe in its app folder (AppPaths.WindowsShippedFolder).
        hint.ShouldContain("dossier app");
        hint.ShouldNotContain("winget");
    }

    [Fact]
    public void The_computer_sound_is_read_from_the_pipe_after_the_screen()
    {
        // The capture starts when ffmpeg opens the pipe: after the screen, and with neither probed at length, the
        // sound starts with the first frame. Before the screen it was 0.8 s off (measured with a flash and a beep).
        Command(new VideoOptions(30, null, true), Pipe).ShouldBe(
        [
            "-hide_banner", "-loglevel", "level+info", "-nostats", "-y",
            "-f", "gdigrab", "-thread_queue_size", "1024", "-probesize", "32", "-analyzeduration", "0",
            "-framerate", "30", "-offset_x", "100", "-offset_y", "200", "-video_size", "640x480", "-draw_mouse", "1",
            "-i", "desktop",
            "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-probesize", "32",
            "-analyzeduration", "0", "-i", @"\\.\pipe\lt",
            "-map", "0:v", "-map", "1:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-ac", "2",
            "-movflags", "+faststart", "out.mp4"
        ]);
    }

    [Fact]
    public void The_microphone_comes_first_and_is_mixed_with_the_pipe()
    {
        var args = Command(new VideoOptions(30, "Mic", true), Pipe);
        IndexOf(args, "-i", "audio=Mic").ShouldBeLessThan(IndexOf(args, "-i", "desktop"));
        IndexOf(args, "-i", "desktop").ShouldBeLessThan(IndexOf(args, "-i", @"\\.\pipe\lt"));
        IndexOf(args, "-filter_complex", "[0:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0,volume=2[a]",
            "-map", "1:v", "-map", "[a]").ShouldNotBe(-1);
    }

    [Fact]
    public void The_pipe_declares_the_device_format_and_the_video_is_stereo()
    {
        var surround = new PipedAudio(@"\\.\pipe\lt", new RawAudioFormat("s24le", 44100, 6));
        var args = Command(new VideoOptions(30, null, true), surround);
        IndexOf(args, "-f", "s24le", "-ar", "44100", "-ac", "6").ShouldNotBe(-1);
        IndexOf(args, "-b:a", "160k", "-ac", "2").ShouldNotBe(-1);
    }

    [Fact]
    public void The_virtual_audio_driver_is_not_recorded() =>
        Command(new VideoOptions(30, "Mic", true), Pipe).ShouldNotContain("audio=virtual-audio-capturer");

    [Fact]
    public void The_computer_sound_needs_its_pipe() =>
        Should.Throw<ArgumentException>(() => new DirectShowGdigrab(() => true).BuildInputs(Area, new VideoOptions(30, null, true), null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_computer_sound_needs_an_audio_output(bool hasOutput) =>
        new DirectShowGdigrab(() => hasOutput).CanRecordSystemAudio([]).ShouldBe(hasOutput);
}
