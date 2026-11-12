namespace ShowTime.Core.Updates;

/// <summary>Where an update stands, from looking for one to replacing the app.</summary>
public enum UpdateStage
{
    /// <summary>Nothing to offer, or not looked for yet.</summary>
    Idle,

    /// <summary>Asking GitHub for the latest release, with nothing offered yet.</summary>
    Checking,

    /// <summary>A newer version can be installed.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded and staged; the install waits for the recording to end.</summary>
    WaitingForRecording,

    /// <summary>Downloaded and staged, but the user kept a window open: a click installs it without downloading again.</summary>
    Downloaded,

    /// <summary>The replacement has started; ShowTime is quitting.</summary>
    Installing,
}

/// <param name="Release">The version offered; null in <see cref="UpdateStage.Idle"/> and <see cref="UpdateStage.Checking"/>.</param>
/// <param name="Progress">The download's progress, from 0 to 1.</param>
public sealed record UpdateState(UpdateStage Stage, ReleaseInfo? Release, double Progress)
{
    public static readonly UpdateState Idle = new(UpdateStage.Idle, null, 0);
}
