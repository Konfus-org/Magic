using Magic.Contexts.Assets;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Components;

/// <summary>
/// The passes applied to everything that is drawn, in the order of <see cref="Passes"/>, over every render target:
/// post-processing is global, not a camera's. It sits on an entity of its own, one per world; of several, the first
/// is followed, warned about once. Nothing runs that is not listed, the tonemap included, so without one the linear
/// scene is shown. A pass is loaded while it is listed and unloaded when it is not.
/// </summary>
public struct PostProcessing : IComponent
{
    public PassList Passes { get; set; }
}

/// <summary>
/// Up to <see cref="Capacity"/> passes in the order they run, inline in the component so it stays plain unmanaged
/// data. The list ends at the first empty handle. Index it like an array; in a chunk it is one:
/// <c>{ "passes": [ { "id": 10030 }, { "id": 10031 } ] }</c>.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(PassListConverter))]
public struct PassList
{
    public const int Capacity = 8;

    private Handle<Pass> _first;

    /// <summary>The passes before the first empty handle.</summary>
    public readonly int Count
    {
        get
        {
            ReadOnlySpan<Handle<Pass>> passes = this;
            int end = passes.IndexOf(Handle<Pass>.None);

            return end < 0 ? Capacity : end;
        }
    }
}
