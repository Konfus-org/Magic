using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Rendering;

namespace Magic.Internal;

/// <summary>
/// One kind of render effect: its type, its size in bytes, whether it is plain data that bytes can hold, and how its
/// bytes, as <see cref="RenderEffects{T}"/> keeps them, are boxed and written.
/// </summary>
internal sealed record RenderEffectKind(
    Type Type,
    int Size,
    bool IsPlainData,
    Func<ReadOnlySpan<byte>, object> Box,
    Action<object, Span<byte>> Write);

/// <summary>
/// The kinds of render effect held so far, each a number <see cref="RenderEffects{T}"/> keeps in place of the type, as
/// a component holds no reference: numbered from 1 as each is first held, for the run. Safe from any thread.
/// </summary>
internal static class RenderEffectKinds
{
    private static readonly Lock _adding = new();

    private static readonly MethodInfo _of = typeof(RenderEffectKinds).GetMethod(
        nameof(Of),
        BindingFlags.NonPublic | BindingFlags.Static,
        Type.EmptyTypes) ?? throw new MissingMethodException(nameof(RenderEffectKinds), nameof(Of));

    /// <summary>
    /// Every kind by its number, the first none: replaced whole as one is added, so a reader on another thread sees one
    /// list.
    /// </summary>
    private static volatile RenderEffectKind?[] _kinds = [null];

    internal static ushort Of<TEffect>()
        where TEffect : struct => Numbered<TEffect>.Number;

    /// <summary>
    /// The number of <paramref name="type"/>, a struct known only at run time, as a file names it.
    /// </summary>
    internal static ushort Of(Type type) => (ushort)(_of.MakeGenericMethod(type).Invoke(null, null) ?? 0);

    internal static RenderEffectKind Get(ushort number)
        => _kinds[number] ?? throw new ArgumentOutOfRangeException(nameof(number), number, "No effect is numbered so.");

    private static ushort Add(RenderEffectKind kind)
    {
        lock (_adding)
        {
            RenderEffectKind?[] kinds = [.. _kinds, kind];

            if (kinds.Length > ushort.MaxValue)
                throw new InvalidOperationException("There are more kinds of effect than can be numbered.");

            _kinds = kinds;

            return (ushort)(kinds.Length - 1);
        }
    }

    /// <summary>
    /// The number of <typeparamref name="TEffect"/>, given the first time it is asked for.
    /// </summary>
    private static class Numbered<TEffect>
        where TEffect : struct
    {
        internal static readonly ushort Number = Add(new RenderEffectKind(
            typeof(TEffect),
            Unsafe.SizeOf<TEffect>(),
            !RuntimeHelpers.IsReferenceOrContainsReferences<TEffect>(),
            static bytes => MemoryMarshal.Read<TEffect>(bytes),
            static (effect, into) =>
            {
                TEffect value = (TEffect)effect;
                MemoryMarshal.Write(into, in value);
            }));
    }
}
