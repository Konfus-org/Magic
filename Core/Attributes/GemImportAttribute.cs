namespace Magic.Attributes;

/// <summary>
/// Marks a public settable property of a gem or exported class that the loader fills from the host container
/// after construction. Required imports (the default) count as dependencies for load ordering and fail the
/// load when nothing provides them; optional imports are left <see langword="null"/> instead.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class GemImportAttribute : Attribute
{
    public bool Required { get; init; } = true;
}
