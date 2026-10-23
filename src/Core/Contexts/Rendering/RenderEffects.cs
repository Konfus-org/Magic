using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Contexts.Rendering;

/// <summary>
/// Up to <see cref="Capacity"/> effects in order, one of each type: what a component holds its effects in
/// (<see cref="Components.PostProcessing"/>, <see cref="Components.Weather"/>). Each is a struct of plain data of at
/// most <see cref="MaxBytes"/> bytes, kept inline, so the component stays plain unmanaged data. A <c>foreach</c>
/// visits them in order: <c>foreach (IPostEffect effect in post.Effects)</c>. As JSON, an array of effects, each
/// naming its type: <c>[ { "type": "Tonemap", "shader": { "id": 7102 }, "bias": 0 } ]</c>.
/// </summary>
[JsonConverter(typeof(RenderEffectsJsonConverter))]
public struct RenderEffects<T> : IEquatable<RenderEffects<T>>
    where T : IRenderEffect
{
    public const int Capacity = RenderEffectSlots.Count;

    public const int MaxBytes = RenderEffectSlots.Bytes;

    /// <summary>
    /// Each effect's kind (<see cref="RenderEffectKinds"/>) in order, none past the last, and its bytes.
    /// </summary>
    private RenderEffectNumbers _kinds;
    private RenderEffectBytes _bytes;

    public readonly int Count
    {
        get
        {
            int count = 0;
            while (count < Capacity && _kinds[count] != 0)
            {
                count++;
            }

            return count;
        }
    }

    public static bool operator ==(RenderEffects<T> left, RenderEffects<T> right) => left.Equals(right);

    public static bool operator !=(RenderEffects<T> left, RenderEffects<T> right) => !left.Equals(right);

    public readonly bool Equals(RenderEffects<T> other)
        => MemoryMarshal.AsBytes<ushort>(_kinds).SequenceEqual(MemoryMarshal.AsBytes<ushort>(other._kinds))
            && ((ReadOnlySpan<byte>)_bytes).SequenceEqual(other._bytes);

    public override readonly bool Equals(object? obj) => obj is RenderEffects<T> other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(MemoryMarshal.AsBytes<ushort>(_kinds));
        hash.AddBytes(_bytes);

        return hash.ToHashCode();
    }

    /// <summary>
    /// The <typeparamref name="TEffect"/> it holds; false when it holds none.
    /// </summary>
    public readonly bool TryGet<TEffect>(out TEffect effect)
        where TEffect : struct, T
    {
        int slot = Find(RenderEffectKinds.Of<TEffect>());
        if (slot < 0)
        {
            effect = default;

            return false;
        }

        effect = MemoryMarshal.Read<TEffect>(ReadBytes(slot));

        return true;
    }

    /// <summary>
    /// Puts <paramref name="effect"/> in place of the <typeparamref name="TEffect"/> it holds, or adds it last. Throws
    /// when it is larger than <see cref="MaxBytes"/>, holds a reference, or there is no room for it.
    /// </summary>
    public void Set<TEffect>(in TEffect effect)
        where TEffect : struct, T
        => MemoryMarshal.Write(Take(RenderEffectKinds.Of<TEffect>()), in effect);

    /// <summary>
    /// Takes the <typeparamref name="TEffect"/> it holds out, those after it moving up; false when it held none.
    /// </summary>
    public bool Remove<TEffect>()
        where TEffect : struct, T
    {
        int slot = Find(RenderEffectKinds.Of<TEffect>());

        if (slot < 0) return false;

        int last = Count - 1;
        for (int i = slot; i < last; i++)
        {
            _kinds[i] = _kinds[i + 1];
            GetBytes(i + 1).CopyTo(GetBytes(i));
        }

        _kinds[last] = 0;
        GetBytes(last).Clear();

        return true;
    }

    /// <summary>
    /// What a <c>foreach</c> visits the effects with, in order, each boxed as it is read.
    /// </summary>
    public readonly Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// <see cref="Set{TEffect}"/> for an effect whose type is known only at run time, as a file names it.
    /// </summary>
    internal void Set(object effect)
    {
        ushort number = RenderEffectKinds.Of(effect.GetType());
        RenderEffectKinds.Get(number).Write(effect, Take(number));
    }

    /// <summary>
    /// The slot an effect of kind <paramref name="number"/> is written into, cleared: the one it holds, else the next;
    /// throws when it cannot hold one.
    /// </summary>
    [UnscopedRef]
    private Span<byte> Take(ushort number)
    {
        RenderEffectKind kind = RenderEffectKinds.Get(number);

        if (kind.Size > MaxBytes)
        {
            throw new ArgumentException(
                $"{kind.Type.Name} is {kind.Size} bytes, over the {MaxBytes} an effect has.",
                nameof(number));
        }

        if (!kind.IsPlainData)
            throw new ArgumentException($"{kind.Type.Name} holds a reference, which no effect can.", nameof(number));

        int slot = Find(number);
        if (slot < 0)
        {
            slot = Count;

            if (slot == Capacity) throw new InvalidOperationException($"It holds {Capacity} effects already.");

            _kinds[slot] = number;
        }

        Span<byte> bytes = GetBytes(slot);
        bytes.Clear();

        return bytes;
    }

    private readonly int Find(ushort number)
    {
        for (int i = 0; i < Capacity && _kinds[i] != 0; i++)
        {
            if (_kinds[i] == number) return i;
        }

        return -1;
    }

    [UnscopedRef]
    private readonly ReadOnlySpan<byte> ReadBytes(int slot)
        => ((ReadOnlySpan<byte>)_bytes).Slice(slot * MaxBytes, MaxBytes);

    [UnscopedRef]
    private Span<byte> GetBytes(int slot) => ((Span<byte>)_bytes).Slice(slot * MaxBytes, MaxBytes);

    /// <summary>
    /// Visits the effects in order, each boxed as it is read.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The enumerator foreach finds is the collection's own, as List<T>.Enumerator is.")]
    [SuppressMessage(
        "Performance",
        "CA1815:Override equals and operator equals on value types",
        Justification = "An enumerator is walked, never compared.")]
    public struct Enumerator(RenderEffects<T> effects)
    {
        private readonly RenderEffects<T> _effects = effects;
        private int _slot = -1;

        public readonly T Current
            => (T)RenderEffectKinds.Get(_effects._kinds[_slot]).Box(_effects.ReadBytes(_slot));

        public bool MoveNext() => ++_slot < Capacity && _effects._kinds[_slot] != 0;
    }
}
