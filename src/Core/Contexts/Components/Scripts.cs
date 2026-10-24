using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Contexts.Components;

/// <summary>
/// The scripts an entity has, each by the <see cref="Script"/> that declares it: up to <see cref="Capacity"/>,
/// unordered, each once, inline so the component stays plain unmanaged data. It is the one record of which scripts an
/// entity runs: a prefab's part gives its entity one from its scripts list, with each script's values beside, and the
/// gem that runs scripts starts a copy of every script here and ends it once its handle is gone, so game code attaches
/// or detaches a script by changing this. As JSON, an array of handles.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(ScriptsJsonConverter))]
public struct Scripts : IComponent, IEquatable<Scripts>
{
    public const int Capacity = 8;

    private Handle<Script> _first;

    public static bool operator ==(Scripts left, Scripts right) => left.Equals(right);

    public static bool operator !=(Scripts left, Scripts right) => !left.Equals(right);

    /// <summary>
    /// Whether both have the same scripts, wherever each holds them.
    /// </summary>
    public readonly bool Equals(Scripts other)
    {
        // Every script this has, the other has too, and the other has no more than this.
        int count = 0;
        foreach (Handle<Script> held in this)
        {
            if (held == Handle<Script>.None) continue;

            if (!other.Has(held)) return false;

            count++;
        }

        foreach (Handle<Script> held in other)
        {
            if (held != Handle<Script>.None) count--;
        }

        return count == 0;
    }

    public override readonly bool Equals(object? obj) => obj is Scripts other && Equals(other);

    public override readonly int GetHashCode()
    {
        // In no order, as Equals ignores which slot holds each.
        ulong hash = 0;
        foreach (Handle<Script> held in this)
        {
            hash ^= held.Id;
        }

        return hash.GetHashCode();
    }

    public readonly bool Has(Handle<Script> script)
        => script != Handle<Script>.None && ((ReadOnlySpan<Handle<Script>>)this).Contains(script);

    /// <summary>
    /// Adds <paramref name="script"/>; false when it is none, there already, or there is no room for it.
    /// </summary>
    public bool Add(Handle<Script> script)
    {
        Span<Handle<Script>> scripts = this;
        int free = scripts.IndexOf(Handle<Script>.None);

        if (script == Handle<Script>.None || Has(script) || free < 0) return false;

        scripts[free] = script;

        return true;
    }

    /// <summary>
    /// Takes <paramref name="script"/> out; false when it was not there.
    /// </summary>
    public bool Remove(Handle<Script> script)
    {
        Span<Handle<Script>> scripts = this;
        int at = scripts.IndexOf(script);

        if (script == Handle<Script>.None || at < 0) return false;

        scripts[at] = Handle<Script>.None;

        return true;
    }
}
