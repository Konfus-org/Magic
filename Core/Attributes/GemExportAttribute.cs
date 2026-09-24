namespace Magic.Attributes;

/// <summary>
/// Marks a class in a gem assembly whose single instance the host creates and registers under the given
/// contracts for other gems and itself to resolve. Constructor parameters are resolved from the container, like
/// the gem class itself (which may also carry this attribute to export itself). Contract types must be declared
/// in the host: each gem lives in its own load context, so a type from one gem is not the same type when another
/// gem names it. Exports implementing <see cref="IDisposable"/> are disposed when the gem unloads.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GemExportAttribute(Type contract, params Type[] moreContracts) : Attribute
{
    public Type[] Contracts { get; } = [contract, .. moreContracts];
}
