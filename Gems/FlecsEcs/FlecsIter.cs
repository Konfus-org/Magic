using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Contexts;
using System.Runtime.InteropServices;

namespace FlecsGem;

/// <summary>
/// Views over one chunk of a flecs iterator. A chunk is a table range: its entities and each component column
/// are contiguous native arrays, so these are spans over that memory and nothing is copied.
/// </summary>
internal static unsafe class FlecsIter
{
    public static ReadOnlySpan<Handle> Entities(flecs.ecs_iter_t* it)
    {
        return new ReadOnlySpan<Handle>(it->entities, it->count);
    }

    public static Span<T> Column<T>(flecs.ecs_iter_t* it, int field) where T : unmanaged
    {
        return new Iter(it).Span<T>(field);
    }

    /// <summary>The first element of a column, for a pointer-style walk with <c>Unsafe.Add</c>.</summary>
    public static ref T First<T>(flecs.ecs_iter_t* it, int field) where T : unmanaged
    {
        return ref MemoryMarshal.GetReference(Column<T>(it, field));
    }
}
