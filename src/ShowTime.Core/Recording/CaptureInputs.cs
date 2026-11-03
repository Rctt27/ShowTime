namespace ShowTime.Core.Recording;

/// <summary>The ffmpeg inputs of a recording, as one platform captures the screen and the sound.</summary>
/// <param name="AudioInputs">
/// The arguments of each audio input, each ending with its "-i". The microphone, when recorded here, comes first: the
/// transcription's track is read from input 0.
/// </param>
/// <param name="VideoInput">The arguments of the screen input, ending with its "-i".</param>
/// <param name="VideoFilter">A filter for the screen stream, such as a crop when the input captures a whole display.</param>
/// <param name="TrailingAudioInputs">
/// Audio inputs opened after the screen, for a source that starts capturing when ffmpeg opens it: ffmpeg starts each
/// input's timestamps at 0, so the order in which they open sets the offset between sound and picture.
/// </param>
/// <param name="DownmixToStereo">Whether to write the sound in stereo, when an input may have more channels.</param>
/// <param name="MicrophoneTrails">
/// The microphone is the first trailing input rather than the first audio input: a pipe fed by ShowTime's own capture.
/// </param>
public sealed record CaptureInputs(
    IReadOnlyList<IReadOnlyList<string>> AudioInputs, IReadOnlyList<string> VideoInput, string? VideoFilter = null,
    IReadOnlyList<IReadOnlyList<string>>? TrailingAudioInputs = null, bool DownmixToStereo = false, bool MicrophoneTrails = false);
