using ShowTime.Core.Geometry;

namespace ShowTime.Core.Recording;

/// <summary>How ffmpeg captures the screen and the sound on one operating system.</summary>
public interface IFfmpegPlatform
{
    /// <summary>
    /// The audio input that records what the computer plays, kept out of the microphones. On Windows, a virtual
    /// audio driver some capture tools install, which ShowTime does not use.
    /// </summary>
    string SystemAudioDevice { get; }

    /// <summary>Whether this computer can record what it plays, given the audio inputs ffmpeg listed.</summary>
    bool CanRecordSystemAudio(IReadOnlyCollection<string> devices);

    /// <summary>What recording the computer's sound needs, for the toggles' tooltips.</summary>
    string SystemAudioRequirement { get; }

    /// <summary>How to install ffmpeg, shown when it cannot be found.</summary>
    string InstallHint { get; }

    /// <summary>Arguments that make ffmpeg list the audio inputs on its standard error.</summary>
    IReadOnlyList<string> ListDevicesArguments { get; }

    /// <summary>Audio input names, as <see cref="BuildInputs"/> expects them.</summary>
    IReadOnlyList<string> ParseDeviceList(string output);

    /// <param name="area">Physical screen pixels, with even dimensions.</param>
    /// <param name="systemAudio">The pipe carrying the computer sound, on a platform that captures it itself.</param>
    /// <param name="microphone">The pipe carrying the microphone, on a platform that captures it itself (macOS).</param>
    CaptureInputs BuildInputs(PixelRect area, VideoOptions options, PipedAudio? systemAudio, PipedAudio? microphone = null);
}
