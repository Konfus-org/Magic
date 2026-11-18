namespace Magic.Worlds;

/// <summary>
/// What the console sets and reads of the time of the world, under the path <c>World</c>.
/// </summary>
internal interface IWorldClock
{
    /// <inheritdoc cref="World.IsPaused"/>
    bool IsPaused { get; set; }

    /// <inheritdoc cref="World.TimeScale"/>
    float TimeScale { get; set; }
}
