namespace Magic.Attributes;

/// <summary>
/// Marks a class in a gem assembly whose single instance the loader creates and offers to the host and to
/// other gems. Constructor parameters are resolved from the host container, just like the gem class itself.
/// <para>
/// Without explicit <paramref name="contracts"/> the instance is registered under every interface it
/// implements, except <see cref="IDisposable"/> and interfaces declared in the gem's own assembly.
/// Contract types must be declared in the host (or another assembly the host already has loaded), never in
/// a gem: each gem lives in its own load context, so a type from one gem's assembly is not the same type
/// when another gem names it.
/// </para>
/// Exports implementing <see cref="IDisposable"/> are disposed when the gem is unloaded.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GemExportAttribute(params Type[] contracts) : Attribute
{
    public Type[] Contracts { get; } = contracts;
}
