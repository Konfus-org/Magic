namespace Magic.Threading;

/// <summary>
/// The engine's own threads, each of which runs for the whole run. Background work is a task, not one of these.
/// </summary>
public enum EngineThread : byte
{
    /// <summary>
    /// What the frames run on, and where the ECS and the gems' hooks are.
    /// </summary>
    Main,
}
