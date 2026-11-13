using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The scripts of an entity, in any language, by their <see cref="Script"/> assets, inline so the component stays
/// plain unmanaged data.
/// </summary>
/// <remarks>
/// <para>
/// It holds up to <see cref="Capacity"/> scripts, in no order, each one time. A free slot holds
/// <see cref="Handle{T}.None"/>. To give an entity a script, add its handle. To take the script off, remove the handle.
/// </para>
/// <para>
/// As JSON, it is an array of handles: <c>"ScriptContainer": [ { "id": 3312 } ]</c>.
/// </para>
/// </remarks>
[InlineArray(Capacity)]
[JsonConverter(typeof(ScriptContainerJsonConverter))]
public struct ScriptContainer : IComponent, IEquatable<ScriptContainer>
{
    public const int Capacity = 8;

    private Handle<Script> _first;

    public static bool operator ==(ScriptContainer left, ScriptContainer right) => left.Equals(right);

    public static bool operator !=(ScriptContainer left, ScriptContainer right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same scripts in the same slots.
    /// </summary>
    public readonly bool Equals(ScriptContainer other) => ((ReadOnlySpan<Handle<Script>>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is ScriptContainer other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        foreach (Handle<Script> script in this)
        {
            hash.Add(script);
        }

        return hash.ToHashCode();
    }

    public readonly bool Has(Handle<Script> script) =>
        script != Handle<Script>.None && ((ReadOnlySpan<Handle<Script>>)this).Contains(script);

    /// <summary>
    /// Adds <paramref name="script"/>; false when it is none, there already, or there is no room for it.
    /// </summary>
    public bool Add(Handle<Script> script)
    {
        Span<Handle<Script>> held = this;
        int free = held.IndexOf(Handle<Script>.None);
        if (script == Handle<Script>.None || held.Contains(script) || free < 0)
            return false;

        held[free] = script;

        return true;
    }

    /// <summary>
    /// Removes <paramref name="script"/>; false when it is not there.
    /// </summary>
    public bool Remove(Handle<Script> script)
    {
        Span<Handle<Script>> held = this;
        int slot = held.IndexOf(script);
        if (script == Handle<Script>.None || slot < 0)
            return false;

        held[slot] = Handle<Script>.None;

        return true;
    }
}
