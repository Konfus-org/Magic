using Magic.Contexts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Services;

/// <summary>
/// A <c>.magic</c> file: an object whose <c>"name"</c> and <c>"gems"</c> are the <see cref="Project"/>'s own and whose
/// every other key is a section a gem reads for itself (<see cref="Project.Sections"/>). What is not in the file keeps
/// the project's default; the paths a project also has are never in the file.
/// </summary>
internal sealed class ProjectConverter : JsonConverter<Project>
{
    public override Project Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a project file is an object.");

        Project project = new();
        string name = project.Name;
        string[] gems = project.Gems;
        Dictionary<string, Unread> sections = new(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString() ?? "";
            reader.Read();
            if (key.Equals(nameof(Project.Name), StringComparison.OrdinalIgnoreCase))
                name = reader.GetString() ?? name;
            else if (key.Equals(nameof(Project.Gems), StringComparison.OrdinalIgnoreCase))
                gems = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? gems;
            else
                sections[key] = new Unread(JsonElement.ParseValue(ref reader));
        }

        return project with { Name = name, Gems = gems, Sections = sections };
    }

    public override void Write(Utf8JsonWriter writer, Project value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WritePropertyName("gems");
        JsonSerializer.Serialize(writer, value.Gems, options);
        foreach ((string key, Unread section) in value.Sections)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, section, options);
        }

        writer.WriteEndObject();
    }
}
