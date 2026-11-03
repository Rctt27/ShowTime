using System.Globalization;
using System.Text.RegularExpressions;
using ShowTime.Core.Geometry;

namespace ShowTime.Core.Recording;

/// <summary>
/// macOS: AVFoundation for the screen; the microphone and the Mac's sound come from ShowTime's own captures through
/// pipes opened after the screen. ffmpeg's avfoundation keeps a single 10 ms buffer of the microphone, overwritten
/// whenever ffmpeg reads late: one recording kept 9.8 s of a 23 s talk, which then played too fast. AVFoundation
/// records a whole display, so the area is cut out of it with a crop filter; an area across two displays keeps the
/// part on the display holding most of it.
/// </summary>
/// <param name="displays">The displays in AVFoundation's order ("Capture screen 0", 1…), in physical pixels.</param>
public sealed partial class AvFoundation(Func<IReadOnlyList<PixelRect>> displays) : IFfmpegPlatform
{
    /// <summary>
    /// The virtual audio driver ShowTime once read the Mac's sound from: no longer recorded, only kept out of the
    /// microphones when it is installed.
    /// </summary>
    public string SystemAudioDevice => "BlackHole 2ch";

    public string SystemAudioRequirement => "l'autorisation Enregistrement de l'écran";

    public string InstallHint => "Il est livré dans ShowTime.app : réinstallez l'app depuis la page des versions, " +
                                 "ou indiquez un autre ffmpeg dans les réglages.";

    public IReadOnlyList<string> ListDevicesArguments { get; } = ["-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""];

    public IReadOnlyList<string> ParseDeviceList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var devices = new List<string>();
        var inAudioSection = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("AVFoundation audio devices", StringComparison.Ordinal))
                inAudioSection = true;
            else if (line.Contains("AVFoundation video devices", StringComparison.Ordinal))
                inAudioSection = false;
            else if (inAudioSection && DeviceLine().Match(line) is { Success: true } match)
                devices.Add(match.Groups[1].Value.TrimEnd('\r'));
        }
        return devices;
    }

    /// <summary>Always: ScreenCaptureKit captures the Mac's sound on every macOS ShowTime runs on.</summary>
    public bool CanRecordSystemAudio(IReadOnlyCollection<string> devices) => true;

    public CaptureInputs BuildInputs(PixelRect area, VideoOptions options, PipedAudio? systemAudio, PipedAudio? microphone = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SystemAudio && systemAudio is null)
            throw new ArgumentException("The computer sound is read from a pipe.", nameof(systemAudio));
        var recordsMicrophone = !string.IsNullOrEmpty(options.Microphone);
        if (recordsMicrophone && microphone is null)
            throw new ArgumentException("The microphone is read from a pipe.", nameof(microphone));
        // The microphone first: it is the first stream of the mix and the transcription's track.
        var pipes = new List<PipedAudio>();
        if (recordsMicrophone)
            pipes.Add(microphone!);
        if (options.SystemAudio)
            pipes.Add(systemAudio!);

        var all = displays();
        var index = 0;
        long best = -1;
        for (var i = 0; i < all.Count; i++)
        {
            var common = all[i].Intersect(area);
            var overlap = (long)common.Width * common.Height;
            if (overlap > best)
                (best, index) = (overlap, i);
        }
        var display = all.Count > 0 ? all[index] : area;
        var crop = (all.Count > 0 ? display.Intersect(area) : area).WithEvenSize();
        var culture = CultureInfo.InvariantCulture;
        // As with gdigrab on Windows: probing the screen would delay the opening of the pipe, whose capture only starts
        // then, and the sound would come out ahead of the picture.
        string[] video =
        [
            "-f", "avfoundation", "-thread_queue_size", "1024", .. pipes.Count == 0 ? [] : NoProbe, "-capture_cursor", "1",
            "-framerate", options.FrameRate.ToString(culture), "-i", $"Capture screen {index.ToString(culture)}:none"
        ];
        var filter = string.Create(culture, $"crop={crop.Width}:{crop.Height}:{crop.X - display.X}:{crop.Y - display.Y}");
        if (pipes.Count == 0)
            return new CaptureInputs([], video, filter);
        // After the screen, so that the sound starts with the first frame.
        return new CaptureInputs([], video, filter, TrailingAudioInputs: [.. pipes.Select(PipeInput)], MicrophoneTrails: recordsMicrophone);
    }

    private static readonly string[] NoProbe = ["-probesize", "32", "-analyzeduration", "0"];

    /// <summary>A pipe's format is declared: there is nothing to probe.</summary>
    private static string[] PipeInput(PipedAudio pipe)
    {
        var culture = CultureInfo.InvariantCulture;
        return
        [
            "-f", pipe.Format.SampleFormat, "-ar", pipe.Format.SampleRate.ToString(culture), "-ac", pipe.Format.Channels.ToString(culture),
            "-thread_queue_size", "1024", .. NoProbe, "-i", pipe.PipePath
        ];
    }

    [GeneratedRegex(@"\] \[\d+\] (.+)$")]
    private static partial Regex DeviceLine();
}
