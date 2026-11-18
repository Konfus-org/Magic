using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Files;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// Reads and writes a property of a part that has a <see cref="CliPathAttribute"/>, named by a console path such as
/// <c>Window.Title</c>.
/// </summary>
/// <remarks>
/// A path is the part's own, then a property and its members to any depth, each named as <see cref="EntityPaths"/>
/// names a member. The part's own path alone is each of its properties.
/// </remarks>
internal static class ServicePaths
{
    /// <summary>
    /// The type that gives <paramref name="part"/> a console path, its class or an interface of it, with that path;
    /// null when none has one.
    /// </summary>
    public static (string Path, Type Type)? FindMarked(Type part)
    {
        foreach (Type type in part.GetInterfaces().Prepend(part))
        {
            if (type.GetCustomAttribute<CliPathAttribute>(inherit: false) is { } marked)
                return (marked.Path, type);
        }

        return null;
    }

    /// <summary>
    /// The value at <paramref name="path"/> under <paramref name="service"/> as JSON, or why there is none.
    /// </summary>
    public static Result<JsonNode?> Read(object service, Type type, string path)
    {
        Result<(object Value, Type Type)> value = EntityPaths.ReadMembers(service, type, path.Split('.').AsSpan(1));
        if (!value.Ok)
            return Result.Failure(value.Error);

        Result<JsonElement> json = Json.Write(value.Value.Value, value.Value.Type);

        return json.Ok ? Result.Success(JsonNode.Parse(json.Value.GetRawText())) : Result.Failure(json.Error);
    }

    /// <summary>
    /// Writes <paramref name="values"/> at <paramref name="path"/> under <paramref name="service"/>, or returns why
    /// not.
    /// </summary>
    public static Result Write(object service, Type type, string path, ReadOnlySpan<string> values)
    {
        string[] members = path.Split('.')[1..];
        if (members.Length == 0)
            return Result.Failure($"{path} is not one value; name one of its properties.");

        if (
            members.Length == 1
            && EntityPaths.FindMember(type, members[0]) is PropertyInfo property
            && !EntityPaths.IsSettable(property)
        )
            return Result.Failure($"{path} is read-only.");

        return EntityPaths.WriteMembers(service, type, members, values);
    }
}
