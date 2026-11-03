using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Tests.Recording;

public sealed class AvFoundationTests
{
    private const string DeviceList = """
        [AVFoundation indev @ 0x7f8b1c004a00] AVFoundation video devices:
        [AVFoundation indev @ 0x7f8b1c004a00] [0] FaceTime HD Camera
        [AVFoundation indev @ 0x7f8b1c004a00] [1] Capture screen 0
        [AVFoundation indev @ 0x7f8b1c004a00] [2] Capture screen 1
        [AVFoundation indev @ 0x7f8b1c004a00] AVFoundation audio devices:
        [AVFoundation indev @ 0x7f8b1c004a00] [0] MacBook Pro Microphone
        [AVFoundation indev @ 0x7f8b1c004a00] [1] BlackHole 2ch
        [AVFoundation indev @ 0x7f8b1c004a00] [2] Microphone (USB) [Studio]
        : Input/output error
        """;

    /// <summary>Two displays side by side, the second at 1.5× on the right.</summary>
    private static readonly IReadOnlyList<PixelRect> Displays = [new(0, 0, 2880, 1800), new(2880, 0, 2560, 1440)];

    private static readonly PipedAudio Pipe = new("/tmp/lt.pcm", new RawAudioFormat("f32le", 48000, 2));

    private static readonly PipedAudio MicPipe = new("/tmp/mic.pcm", new RawAudioFormat("f32le", 48000, 1));

    private static readonly string[] MicPipeInput =
        ["-f", "f32le", "-ar", "48000", "-ac", "1", "-thread_queue_size", "1024", "-probesize", "32", "-analyzeduration", "0", "-i", "/tmp/mic.pcm"];

    private static readonly string[] SystemPipeInput =
        ["-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-probesize", "32", "-analyzeduration", "0", "-i", "/tmp/lt.pcm"];

    private readonly AvFoundation _platform = new(() => Displays);

    [Fact]
    public void Audio_devices_are_those_after_the_audio_heading() =>
        _platform.ParseDeviceList(DeviceList).ShouldBe(["MacBook Pro Microphone", "BlackHole 2ch", "Microphone (USB) [Studio]"]);

    [Fact]
    public void Lists_devices_with_avfoundation() =>
        _platform.ListDevicesArguments.ShouldBe(["-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""]);

    /// <summary>No longer recorded: only kept out of the microphones when it is installed.</summary>
    [Fact]
    public void BlackHole_is_kept_out_of_the_microphones() => _platform.SystemAudioDevice.ShouldBe("BlackHole 2ch");

    [Fact]
    public void The_display_holding_the_area_is_recorded_and_cropped_to_it()
    {
        var inputs = _platform.BuildInputs(new PixelRect(3000, 100, 640, 480), new VideoOptions(30, null, false), null);

        inputs.AudioInputs.ShouldBeEmpty();
        inputs.VideoInput.ShouldBe(["-f", "avfoundation", "-thread_queue_size", "1024", "-capture_cursor", "1", "-framerate", "30", "-i", "Capture screen 1:none"]);
        inputs.VideoFilter.ShouldBe("crop=640:480:120:100");
    }

    [Fact]
    public void The_microphone_is_read_from_its_pipe_after_the_screen()
    {
        // Deliberate change: ffmpeg's avfoundation keeps one 10 ms buffer of the microphone and loses the next ones
        // whenever it reads late; ShowTime captures the microphone itself and pipes it, like the Mac's sound.
        var inputs = _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(24, "MacBook Pro Microphone", false), null, MicPipe);

        inputs.AudioInputs.ShouldBeEmpty();
        inputs.TrailingAudioInputs.ShouldBe([MicPipeInput]);
        inputs.MicrophoneTrails.ShouldBeTrue();
        inputs.VideoInput.ShouldContain("-probesize");
        inputs.TrailingAudioInputs!.SelectMany(a => a).ShouldNotContain(":MacBook Pro Microphone");
    }

    [Fact]
    public void The_microphone_comes_before_the_computer_sound()
    {
        var inputs = _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(30, "MacBook Pro Microphone", true), Pipe, MicPipe);
        inputs.TrailingAudioInputs.ShouldBe([MicPipeInput, SystemPipeInput]);
    }

    [Fact]
    public void The_microphone_needs_its_pipe() =>
        Should.Throw<ArgumentException>(() =>
            _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(30, "MacBook Pro Microphone", false), null));

    [Fact]
    public void The_computer_sound_is_read_from_the_pipe_after_the_screen()
    {
        // Deliberate change: ShowTime captures the Mac's sound itself and pipes it; BlackHole is no longer read.
        var inputs = _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(30, null, true), Pipe);

        inputs.AudioInputs.ShouldBeEmpty();
        inputs.TrailingAudioInputs.ShouldBe([SystemPipeInput]);
        inputs.MicrophoneTrails.ShouldBeFalse();
        inputs.VideoInput.ShouldBe(
        [
            "-f", "avfoundation", "-thread_queue_size", "1024", "-probesize", "32", "-analyzeduration", "0",
            "-capture_cursor", "1", "-framerate", "30", "-i", "Capture screen 0:none"
        ]);
        inputs.TrailingAudioInputs!.SelectMany(a => a).ShouldNotContain(":BlackHole 2ch");
    }

    [Fact]
    public void Without_the_computer_sound_the_screen_is_probed_as_before()
    {
        var inputs = _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(30, null, false), Pipe);
        inputs.VideoInput.ShouldNotContain("-probesize");
        inputs.TrailingAudioInputs.ShouldBeNull();
    }

    [Fact]
    public void The_computer_sound_needs_its_pipe() =>
        Should.Throw<ArgumentException>(() => _platform.BuildInputs(new PixelRect(0, 0, 100, 100), new VideoOptions(30, null, true), null));

    [Fact]
    public void An_area_over_two_displays_records_the_one_holding_most_of_it()
    {
        var inputs = _platform.BuildInputs(new PixelRect(2700, 0, 400, 300), new VideoOptions(30, null, false), null);
        inputs.VideoInput[^1].ShouldBe("Capture screen 1:none");
        // One display only: the part of the area on it, with even dimensions.
        inputs.VideoFilter.ShouldBe("crop=220:300:0:0");
    }

    [Fact]
    public void The_install_hint_says_ffmpeg_comes_with_ShowTime()
    {
        _platform.InstallHint.ShouldContain("livré dans ShowTime.app");
        _platform.InstallHint.ShouldNotContain("brew", Case.Insensitive);
    }

    [Fact]
    public void The_computer_sound_needs_no_driver()
    {
        // Deliberate change: ShowTime captures the Mac's sound itself; BlackHole is no longer needed.
        _platform.CanRecordSystemAudio([]).ShouldBeTrue();
        _platform.CanRecordSystemAudio(["MacBook Pro Microphone"]).ShouldBeTrue();
        _platform.SystemAudioRequirement.ShouldNotContain("BlackHole");
    }
}
