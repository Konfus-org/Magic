using System.Reflection;

namespace Magic.Utils;

/// <summary>
/// What a <see cref="Type"/> known only at run time gives.
/// </summary>
internal static class TypeExtensions
{
    /// <summary>
    /// The default of <paramref name="type"/>: a new instance, as its constructor without arguments and its
    /// initializers make it, or why it cannot be made without values.
    /// </summary>
    /// <remarks>
    /// It makes the instance on each call and keeps nothing, so the load context of a gem or of the project unloads
    /// with its types.
    /// </remarks>
    public static Result<object> CreateDefault(this Type type)
    {
        try
        {
            return Activator.CreateInstance(type) is { } made
                ? Result.Success(made)
                : Result.Failure($"a {type.Name} has no value without values.");
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            return Result.Failure($"a {type.Name} cannot be made without values: {ex.Message}");
        }
    }
}
