using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Contexts;
using System.Runtime.InteropServices;

namespace FlecsGem;

/// <summary>
/// Views over one chunk of a flecs iterator. A chunk is a table range: its entities and each component column
/// are contiguous native arrays, so these are spans over that memory and nothing is copied. A field read from
/// the parent (a cascade term) is one shared value: its span has one element, or none when the optional
/// parent is absent (a root entity's chunk).
/// </summary>
internal static unsafe class FlecsIter
{
    public static ReadOnlySpan<Handle> Entities(flecs.ecs_iter_t* it)
    {
        return new ReadOnlySpan<Handle>(it->entities, it->count);
    }

    public static Span<T> Column<T>(flecs.ecs_iter_t* it, int field) where T : unmanaged
    {
        Iter iter = new(it);
        if (!iter.IsSet(field))
            return default;

        if (iter.IsSelf(field))
            return iter.Span<T>(field);

        return new Span<T>(iter.Field<T>(field).Data, 1);
    }

    /// <summary>
    /// The first element of a column, for a pointer-style walk with <c>Unsafe.Add(ref first, i * stride)</c>:
    /// <paramref name="stride"/> is 1 for a per-entity column and 0 for a shared or absent one.
    /// </summary>
    public static ref T First<T>(flecs.ecs_iter_t* it, int field, out int stride) where T : unmanaged
    {
        Span<T> column = Column<T>(it, field);
        if (column.IsEmpty)
        {
            stride = 0;
            Scratch<T>.Value = default;
            return ref Scratch<T>.Value;
        }

        stride = column.Length == it->count ? 1 : 0;

        return ref MemoryMarshal.GetReference(column);
    }

    /// <summary>What an absent optional field reads as in Each(): a default that nobody keeps.</summary>
    private static class Scratch<T> where T : unmanaged
    {
        [ThreadStatic]
        public static T Value;
    }
}
