namespace Magic.Platform;

/// <summary>
/// The OS's events, as a platform gem takes them: the platform thread pumps them again and again for the whole run,
/// apart from the frames, so the window answers the OS whatever the game is doing. The gem hands the game thread what
/// they make as data.
/// </summary>
public interface IPlatform
{
    /// <summary>
    /// Takes the OS's events since the last pump, waiting a little for one when there are none. Platform thread.
    /// </summary>
    void Pump();
}
