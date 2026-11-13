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
/// As JSON, it is an array of slots, each the handle of the script with the values of the script beside it:
/// <c>"ScriptContainer": [ { "id": 3312, "height": 1 } ]</c>. <c>id</c> and <c>started</c> are the names that a slot
/// itself takes, so they are no values for a script.
/// </para>
/// </remarks>
[InlineArray(Capacity)]
[JsonConverter(typeof(ScriptContainerJsonConverter))]
public struct ScriptContainer : IComponent, IEquatable<ScriptContainer>
{
    public const int Capacity = 8;

    private ScriptSlot _first;

    public static bool operator ==(ScriptContainer left, ScriptContainer right) => left.Equals(right);

    public static bool operator !=(ScriptContainer left, ScriptContainer right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same scripts, with the same values, in the same slots.
    /// </summary>
    public readonly bool Equals(ScriptContainer other) => ((ReadOnlySpan<ScriptSlot>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is ScriptContainer other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        foreach (ScriptSlot slot in this)
        {
            hash.Add(slot);
        }

        return hash.ToHashCode();
    }

    public readonly bool Has(Handle<Script> script) => script != Handle<Script>.None && Slot(script) >= 0;

    /// <summary>
    /// Adds <paramref name="script"/>, with <paramref name="values"/> for the gem of its language to set on it; false
    /// when it is none, there already, or there is no room for it.
    /// </summary>
    public bool Add(Handle<Script> script, ScriptValues values = default)
    {
        Span<ScriptSlot> held = this;

        // A free slot holds no script.
        int free = Slot(Handle<Script>.None);
        if (script == Handle<Script>.None || Has(script) || free < 0)
            return false;

        held[free] = new ScriptSlot(script, values);

        return true;
    }

    /// <summary>
    /// Puts <paramref name="values"/> in the slot of <paramref name="script"/> and marks the copy of it started, so a
    /// save keeps the state of the copy and a copy made from that save takes it up instead of starting over; false
    /// when the script is not there.
    /// </summary>
    public bool Keep(Handle<Script> script, ScriptValues values)
    {
        Span<ScriptSlot> held = this;
        int slot = Slot(script);
        if (script == Handle<Script>.None || slot < 0)
            return false;

        held[slot] = new ScriptSlot(script, values, Started: true);

        return true;
    }

    /// <summary>
    /// Removes <paramref name="script"/> and its values; false when it is not there.
    /// </summary>
    public bool Remove(Handle<Script> script)
    {
        Span<ScriptSlot> held = this;
        int slot = Slot(script);
        if (script == Handle<Script>.None || slot < 0)
            return false;

        held[slot] = default;

        return true;
    }

    /// <summary>
    /// The slot that holds <paramref name="script"/>, or -1 when none does.
    /// </summary>
    private readonly int Slot(Handle<Script> script)
    {
        ReadOnlySpan<ScriptSlot> held = this;
        for (int slot = 0; slot < held.Length; slot++)
        {
            if (held[slot].Script == script)
                return slot;
        }

        return -1;
    }
}

/// <summary>
/// One script of an entity: the <see cref="Script"/> asset, the values that a prefab gives it or that a save kept of
/// its copy, and whether that copy had started when the save took it.
/// </summary>
public readonly record struct ScriptSlot(Handle<Script> Script, ScriptValues Values, bool Started = false);
