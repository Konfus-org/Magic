using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Ecs.Components.Effects;

/// <summary>
/// One kind of weather effect: its type, its size in bytes, whether it is plain data that bytes can hold, and how its
/// bytes, as <see cref="WeatherEffects"/> keeps them, are boxed and written.
/// </summary>
internal sealed record WeatherEffectKind(
    Type Type,
    int Size,
    bool IsPlainData,
    Func<ReadOnlySpan<byte>, object> Box,
    Action<object, Span<byte>> Write
);

/// <summary>
/// The kinds of weather effect held so far, each a number <see cref="WeatherEffects"/> keeps in place of the type, as
/// a component holds no reference: numbered from 1 as each is first held, for the run. Safe from any thread.
/// </summary>
internal static class WeatherEffectKinds
{
    private static readonly Lock _adding = new();

    private static readonly MethodInfo _getNumber =
        typeof(WeatherEffectKinds).GetMethod(
            nameof(GetNumber),
            BindingFlags.NonPublic | BindingFlags.Static,
            Type.EmptyTypes
        ) ?? throw new MissingMethodException(nameof(WeatherEffectKinds), nameof(GetNumber));

    /// <summary>
    /// Every kind by its number, the first none: replaced whole as one is added, so a reader on another thread sees one
    /// list.
    /// </summary>
    private static volatile WeatherEffectKind?[] _kinds = [null];

    internal static ushort GetNumber<TEffect>()
        where TEffect : struct => Numbered<TEffect>.Number;

    /// <summary>
    /// The number of <paramref name="type"/>, a struct known only at run time, as a file names it.
    /// </summary>
    internal static ushort GetNumber(Type type) => (ushort)(_getNumber.MakeGenericMethod(type).Invoke(null, null) ?? 0);

    internal static WeatherEffectKind Get(ushort number) =>
        _kinds[number] ?? throw new ArgumentOutOfRangeException(nameof(number), number, "No effect is numbered so.");

    private static ushort Add(WeatherEffectKind kind)
    {
        lock (_adding)
        {
            WeatherEffectKind?[] kinds = [.. _kinds, kind];

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
        internal static readonly ushort Number = Add(
            new WeatherEffectKind(
                typeof(TEffect),
                Unsafe.SizeOf<TEffect>(),
                !RuntimeHelpers.IsReferenceOrContainsReferences<TEffect>(),
                static bytes => MemoryMarshal.Read<TEffect>(bytes),
                static (effect, into) =>
                {
                    TEffect value = (TEffect)effect;
                    MemoryMarshal.Write(into, in value);
                }
            )
        );
    }
}
