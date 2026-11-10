namespace Magic.Configuration;

/// <summary>
/// A settings member its gem reads only as it is made: changing it restarts the run, so the gem is made again with it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequireRestartAttribute : Attribute;
