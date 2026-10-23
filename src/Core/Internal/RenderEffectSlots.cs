using System.Runtime.CompilerServices;
using Magic.Contexts.Rendering;

namespace Magic.Internal;

/// <summary>
/// Each slot's effect kind, inline.
/// </summary>
[InlineArray(RenderEffectSlots.Count)]
internal struct RenderEffectNumbers
{
    private ushort _first;
}

/// <summary>
/// Each slot's effect's bytes, inline.
/// </summary>
[InlineArray(RenderEffectSlots.Count * RenderEffectSlots.Bytes)]
internal struct RenderEffectBytes
{
    private byte _first;
}

/// <summary>
/// The room a <see cref="RenderEffects{T}"/> has: how many effects, and how many bytes each.
/// </summary>
internal static class RenderEffectSlots
{
    internal const int Count = 8;
    internal const int Bytes = 64;
}
