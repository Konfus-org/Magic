using System.CommandLine;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>query components</c>: prints each component type that the run knows, with each field's name, type and default
/// value.
/// </summary>
/// <remarks>
/// The domain query writes only what differs from a default, as a prefab file does, so a tool reads the defaults
/// from here instead of holding a copy of them. The types are Magic's own and each loaded gem's.
/// </remarks>
internal static class ComponentsQuery
{
    public static Command Create(IFileSystem files, Option<string> output)
    {
        Command components = new(
            "components",
            "Prints each component type that the run knows, and the material types, with each field's name, type and default value"
        );
        components.SetAction(parsed => Describe(parsed.GetValue(output)));

        return components;

        void Describe(string? file)
        {
            JsonObject described = new()
            {
                ["components"] = DescribeTypes(Assemblies.GetAll<IComponent>()),
                ["assets"] = DescribeTypes([typeof(Material), typeof(MaterialParam)]),
            };
            CommandLookups.Print(files, file, described, "The component types are in");
        }
    }

    /// <summary>
    /// Each type of <paramref name="types"/>: its name, and each field that a file can set.
    /// </summary>
    private static JsonArray DescribeTypes(IEnumerable<Type> types)
    {
        JsonArray described = [];
        foreach (Type type in types)
        {
            // What each field holds before anything sets one: a new instance, written with every value.
            Result<object> made = type.CreateDefault();
            Result<JsonElement> written = made.Ok ? Json.Write(made.Value, type) : Result.Failure(made.Error);
            if (!written.Ok)
            {
                Log.Info($"{type.Name} is left out: {written.Error}");

                continue;
            }

            JsonNode? defaults = JsonNode.Parse(written.Value.GetRawText());
            JsonObject held = new() { ["type"] = type.Name, ["default"] = defaults?.DeepClone() };
            if (defaults is JsonObject fields)
                held["fields"] = DescribeFields(type, fields);

            described.Add(held);
        }

        return described;
    }

    /// <summary>
    /// Each field of <paramref name="type"/> that a file sets: the name that the JSON uses, the type, the default from
    /// <paramref name="defaults"/>, and the values of a field that is one of a set.
    /// </summary>
    /// <remarks>
    /// It reads the fields as the query command and the prefab reader do: the public fields and the properties that can
    /// be read. A property that follows from others is left out, because a file cannot set it.
    /// </remarks>
    private static JsonArray DescribeFields(Type type, JsonObject defaults)
    {
        JsonArray described = [];
        foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            bool isSettable = member switch
            {
                FieldInfo field => !field.IsInitOnly,
                PropertyInfo property => property.CanRead
                    && property.CanWrite
                    && property.GetIndexParameters().Length == 0,
                _ => false,
            };
            if (!isSettable)
                continue;

            string name = JsonNamingPolicy.CamelCase.ConvertName(member.Name);
            if (defaults[name] is not { } value)
                continue;

            Type held = ValueText.GetValueType(member);
            JsonObject row = new()
            {
                ["name"] = name,
                ["type"] = held.Name,
                ["default"] = value.DeepClone(),
            };
            // The values of a field that is one of a set, as the JSON writes them.
            if (held.IsEnum)
            {
                row["values"] = new JsonArray([
                    .. Enum.GetNames(held).Select(name => (JsonNode?)JsonNamingPolicy.SnakeCaseLower.ConvertName(name)),
                ]);
            }

            described.Add(row);
        }

        return described;
    }
}
