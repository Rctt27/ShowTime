using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Ports;

/// <summary>
/// The recording bar (audio toggles, Start, elapsed time, Stop, Cancel) and, for an area, the frame around it. Both
/// stay visible on screen but are excluded from the video.
/// </summary>
public interface IRecordingView : IDisposable
{
    event EventHandler? StartRequested;
    event EventHandler? StopRequested;
    event EventHandler? CancelRequested;

    /// <param name="startShortcut">Shown on the Start button, or null when it could not be registered.</param>
    /// <param name="stopShortcut">Shown on the Stop button, or null.</param>
    void ShowReady(string? startShortcut, string? stopShortcut);

    /// <summary>Enables what this computer can record, once ffmpeg has listed the audio inputs.</summary>
    void SetAudioDevices(IReadOnlyCollection<string> devices);

    void ShowCountdown(int seconds);

    /// <summary>ffmpeg is launched but not capturing yet: Stop already works, the counter waits.</summary>
    void ShowStarting();

    /// <summary>Capture has begun: what is said from now on is in the video.</summary>
    void ShowRecording();

    void ShowFinishing();

    /// <summary>During the recording: the trigger to hold to highlight, or null when there is none.</summary>
    void ShowHighlightShortcut(string? shortcut);
}

public interface IRecordingViewFactory
{
    /// <summary>Shows the bar near the area, and the frame around it unless the whole screen is recorded.</summary>
    IRecordingView Show(PixelRect area, bool fullScreen);
}

/// <summary>One ffmpeg process recording the screen to an MP4 file.</summary>
public interface IVideoRecorder : IDisposable
{
    string OutputPath { get; }

    /// <summary>The last warnings and errors ffmpeg printed.</summary>
    string ErrorTail { get; }

    /// <summary>
    /// True once the microphone and the screen are actually being recorded, about 0.3 s after the start; false if
    /// ffmpeg exited before. Until then, anything said would be missing from the video.
    /// </summary>
    Task<bool> Capturing { get; }

    /// <summary>Asks ffmpeg to finish the file; true when a playable file was written.</summary>
    Task<bool> StopAsync();

    /// <summary>Stops immediately; the file is left incomplete.</summary>
    void Kill();
}

public interface IVideoRecorderFactory
{
    /// <param name="onUnexpectedExit">
    /// ffmpeg stopped on its own (busy microphone, full disk…). Called on a worker thread. Passed here rather than
    /// exposed as an event: ffmpeg can fail before a subscriber would be attached.
    /// </param>
    IVideoRecorder Start(string ffmpegPath, PixelRect area, string outputPath, VideoOptions options, Action<IVideoRecorder> onUnexpectedExit);
}

/// <summary>What the computer plays, on a platform where ShowTime captures it itself instead of ffmpeg (Windows).</summary>
public interface ISystemAudioSource
{
    /// <summary>Whether there is an audio output to capture.</summary>
    bool IsAvailable();

    /// <summary>
    /// Opens the default output and a pipe for ffmpeg. Capture starts when ffmpeg opens the pipe: sound captured
    /// before ffmpeg reads would come out ahead of the picture.
    /// </summary>
    IAudioCapture Start();
}

/// <summary>
/// The microphone, on a platform where ShowTime captures it itself instead of ffmpeg (macOS: ffmpeg's avfoundation
/// keeps a single 10 ms buffer of it and loses the next ones whenever it reads late).
/// </summary>
public interface IMicrophoneSource
{
    /// <summary>
    /// Opens <paramref name="microphone"/>, named as <see cref="Recording.IFfmpegPlatform.ParseDeviceList"/> names it,
    /// and a pipe for ffmpeg. The sound written to the pipe starts when ffmpeg opens it.
    /// </summary>
    IAudioCapture Start(string microphone);
}

/// <summary>The sound of one recording, written to a pipe until disposed.</summary>
public interface IAudioCapture : IDisposable
{
    PipedAudio Input { get; }
}

public interface IAudioDeviceLister
{
    /// <summary>The audio inputs ffmpeg can record, or an empty list when it cannot list them.</summary>
    Task<IReadOnlyList<string>> ListAsync(string ffmpegPath);
}
