namespace Magic.Attributes;

/// <summary>
/// Marks a setting its gem reads only as it starts, the GPU device opened say, so that changing it, by
/// <see cref="Services.Settings.Set{T}"/> or a preset, ends the run and starts it again at once with every setting as
/// it is (<see cref="Services.Lifetime.Restart"/>): the world is saved and put back, so the game goes on where it
/// was. On a settings class, it marks every setting in it. A settings window says so on the row.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class RequireRestartAttribute : Attribute;
