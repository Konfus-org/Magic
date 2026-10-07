using System.Text.Json;

namespace Magic.Contexts;

/// <summary>
/// A value read from a file and not given a type yet, because only whoever uses it knows which: a setting before its
/// settings class is loaded, a chunk's component or script values by name, a project section a gem reads. It is
/// <see cref="Services.AssetJson"/>'s to read (<see cref="Services.AssetJson.Read{T}(Unread)"/>) and to make
/// (<see cref="Services.AssetJson.Write{T}(T)"/>); written to a file again, it is what was read.
/// </summary>
public readonly struct Unread
{
    internal Unread(JsonElement value)
    {
        Value = value;
    }

    /// <summary>
    /// Nothing was read: the default.
    /// </summary>
    public bool IsEmpty => Value.ValueKind == JsonValueKind.Undefined;

    internal JsonElement Value { get; }

    /// <summary>
    /// The value as it was written, for a message.
    /// </summary>
    public override string ToString()
    {
        return IsEmpty ? "" : Value.GetRawText();
    }
}
