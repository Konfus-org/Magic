namespace Magic.Configuration;

/// <summary>
/// A settings member that its gem reads only when the gem is made.
/// </summary>
/// <remarks>
/// A change to it restarts the run, so the gem is made again with the new value.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequireRestartAttribute : Attribute;
