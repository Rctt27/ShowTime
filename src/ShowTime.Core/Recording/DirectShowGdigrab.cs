using System.Globalization;
using System.Text.RegularExpressions;
using ShowTime.Core.Geometry;

namespace ShowTime.Core.Recording;

/// <summary>
/// Windows: DirectShow for the microphone, gdigrab for the screen, and a pipe for the computer sound, which ShowTime
/// captures itself (WASAPI loopback).
/// </summary>
/// <param name="hasAudioOutput">Whether the computer has an audio output to capture.</param>
public sealed partial class DirectShowGdigrab(Func<bool> hasAudioOutput) : IFfmpegPlatform
{
    /// <summary>A virtual audio driver some capture tools install, listed by dshow: not a microphone.</summary>
    public string SystemAudioDevice => "virtual-audio-capturer";

    public string SystemAudioRequirement => "une sortie audio (haut-parleurs ou casque)";

    public string InstallHint => "Il est livré avec ShowTime : extrayez de nouveau tout le zip, en gardant le dossier app " +
                                 "(qui contient ffmpeg.exe) à côté de ShowTime.exe, ou indiquez un autre ffmpeg dans les réglages.";

    public IReadOnlyList<string> ListDevicesArguments { get; } =
        ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"];

    /// <summary>
    /// Recent builds tag each device: [dshow @ …] "Name" (audio). Older ones list devices under a "DirectShow audio
    /// devices" heading, each followed by an "Alternative name" line.
    /// </summary>
    public IReadOnlyList<string> ParseDeviceList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var devices = new List<string>();
        var inAudioSection = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("DirectShow audio devices", StringComparison.Ordinal))
                inAudioSection = true;
            else if (line.Contains("DirectShow video devices", StringComparison.Ordinal))
                inAudioSection = false;

            var match = DeviceLine().Match(line);
            if (!match.Success || line.Contains("Alternative name", StringComparison.Ordinal))
                continue;
            var kind = match.Groups[3].Value;
            if (kind == "audio" || (kind.Length == 0 && inAudioSection))
                devices.Add(match.Groups[1].Value);
        }
        return devices.Distinct().ToList();
    }

    public bool CanRecordSystemAudio(IReadOnlyCollection<string> devices) => hasAudioOutput();

    public CaptureInputs BuildInputs(PixelRect area, VideoOptions options, PipedAudio? systemAudio)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SystemAudio && systemAudio is null)
            throw new ArgumentException("The computer sound is read from a pipe.", nameof(systemAudio));
        var piped = options.SystemAudio ? systemAudio : null;

        var audio = new List<IReadOnlyList<string>>();
        if (!string.IsNullOrEmpty(options.Microphone))
            audio.Add(MicrophoneInput(options.Microphone));

        // gdigrab takes physical coordinates on the virtual desktop, like the rest of ShowTime.
        var video = new List<string> { "-f", "gdigrab", "-thread_queue_size", "1024" };
        // gdigrab stamps its first frame when it opens, then ffmpeg probes about 20 frames (0.6 s at 30 fps) before
        // opening the pipe, whose capture only starts then: the sound would come out that far ahead of the picture.
        if (piped is not null)
            video.AddRange(NoProbe);
        video.AddRange(
        [
            "-framerate", Invariant(options.FrameRate), "-offset_x", Invariant(area.X), "-offset_y", Invariant(area.Y),
            "-video_size", $"{Invariant(area.Width)}x{Invariant(area.Height)}", "-draw_mouse", "1", "-i", "desktop"
        ]);

        if (piped is not { Format: var format })
            return new CaptureInputs(audio, video);
        // After the screen, so that the sound starts with the first frame; its format is declared, nothing to probe.
        string[] pipe =
        [
            "-f", format.SampleFormat, "-ar", Invariant(format.SampleRate), "-ac", Invariant(format.Channels),
            "-thread_queue_size", "1024", .. NoProbe, "-i", piped.PipePath
        ];
        // The output may be 5.1; a screen recording is played back in stereo.
        return new CaptureInputs(audio, video, TrailingAudioInputs: [pipe], DownmixToStereo: true);
    }

    private static readonly string[] NoProbe = ["-probesize", "32", "-analyzeduration", "0"];

    private static string[] MicrophoneInput(string device) =>
        ["-f", "dshow", "-thread_queue_size", "1024", "-audio_buffer_size", "50", "-i", "audio=" + device];

    /// <summary>Some cultures write negative numbers with a Unicode minus sign, which ffmpeg rejects.</summary>
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("\"([^\"]+)\"\\s*(\\((audio|video|none)\\))?")]
    private static partial Regex DeviceLine();
}
