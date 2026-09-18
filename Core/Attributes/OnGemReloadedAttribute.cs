namespace Magic.Attributes;

/// <summary>
/// Marks a <c>void Method(byte[] state)</c> on the gem class that is called after a hot reload has
/// constructed the gem again, with the state its <see cref="OnGemReloadingAttribute"/> method returned.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class OnGemReloadedAttribute : Attribute
{
}
