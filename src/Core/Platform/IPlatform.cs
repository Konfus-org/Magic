namespace Magic.Platform;

/// <summary>
/// Takes the OS events for a platform gem.
/// </summary>
/// <remarks>
/// The platform thread pumps the events again and again for the whole run, apart from the frames. Thus the window
/// answers the OS whatever the game does. The gem gives the result to the game thread as data.
/// </remarks>
public interface IPlatform
{
    /// <summary>
    /// Takes the OS events since the last pump, and waits a short time for one if there are none.
    /// </summary>
    /// <remarks>
    /// Call it only on the platform thread.
    /// </remarks>
    void Pump();
}
