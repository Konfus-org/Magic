using Magic.Contexts.Assets;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Components;

/// <summary>
/// The posts applied to everything that is drawn, in the order of <see cref="Posts"/>, over every render target:
/// post-processing is global, not a camera's. It sits on an entity of its own, one per world; of several, the first
/// is followed, warned about once. Nothing runs that is not listed, the tonemap included, so without one the linear
/// scene is shown. A post is loaded while it is listed and unloaded when it is not.
/// </summary>
public struct PostProcessing : IComponent
{
    public PostList Posts { get; set; }
}

/// <summary>
/// Up to <see cref="Capacity"/> posts in the order they run, inline in the component so it stays plain unmanaged
/// data. The list ends at the first empty handle. Index it like an array; in a chunk it is one:
/// <c>{ "posts": [ { "id": 10030 }, { "id": 10031 } ] }</c>.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(PostListConverter))]
public struct PostList
{
    public const int Capacity = 8;

    private Handle<Post> _first;

    /// <summary>
    /// The posts before the first empty handle.
    /// </summary>
    public readonly int Count
    {
        get
        {
            ReadOnlySpan<Handle<Post>> posts = this;
            int end = posts.IndexOf(Handle<Post>.None);

            return end < 0 ? Capacity : end;
        }
    }
}
