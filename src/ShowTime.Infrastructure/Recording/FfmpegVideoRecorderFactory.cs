using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;

namespace ShowTime.Infrastructure.Recording;

/// <summary>Records the screen with this platform's ffmpeg inputs.</summary>
/// <param name="systemAudio">The computer sound, on a platform where ShowTime captures it itself (Windows, macOS).</param>
/// <param name="microphone">The microphone, on a platform where ShowTime captures it itself (macOS).</param>
public sealed class FfmpegVideoRecorderFactory(
    IFfmpegPlatform platform, ISystemAudioSource? systemAudio = null, IMicrophoneSource? microphone = null) : IVideoRecorderFactory
{
    public IVideoRecorder Start(string ffmpegPath, PixelRect area, string outputPath, VideoOptions options, Action<IVideoRecorder> onUnexpectedExit)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Before ffmpeg starts: the pipes must be waiting when ffmpeg opens them.
        var captures = new AudioCaptures();
        try
        {
            var system = captures.Add(options.SystemAudio ? systemAudio?.Start() : null);
            var mic = captures.Add(string.IsNullOrEmpty(options.Microphone) ? null : microphone?.Start(options.Microphone));
            return FfmpegVideoRecorder.Start(ffmpegPath, platform.BuildInputs(area, options, system?.Input, mic?.Input), outputPath,
                onUnexpectedExit, captures, options.MicrophoneTrackPath);
        }
        catch
        {
            captures.Dispose();
            throw;
        }
    }

    /// <summary>The captures feeding one recording's inputs, freed together once ffmpeg is done with them.</summary>
    private sealed class AudioCaptures : IDisposable
    {
        private readonly List<IAudioCapture> _captures = [];

        public IAudioCapture? Add(IAudioCapture? capture)
        {
            if (capture is not null)
                _captures.Add(capture);
            return capture;
        }

        public void Dispose()
        {
            foreach (var capture in _captures)
                capture.Dispose();
        }
    }
}
