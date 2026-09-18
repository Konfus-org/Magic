namespace Magic.Attributes;

/// <summary>
/// Marks a <c>byte[] Method()</c> on the gem class that is called just before a hot reload unloads the gem.
/// Whatever it returns is handed to the <see cref="OnGemReloadedAttribute"/> method of the freshly loaded gem.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class OnGemReloadingAttribute : Attribute
{
}
