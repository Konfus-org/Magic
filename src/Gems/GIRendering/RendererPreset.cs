using System.Collections.Immutable;

namespace Magic.GIRendering;

/// <summary>
/// How much the renderer does a frame, set with <c>set GIRendering.preset=&lt;name&gt;</c>: the share of the
/// output the picture is drawn at and resolved up from, the cards lit a frame, whether the sharp reflections are
/// traced, and the pixels across each reflection ray stands for.
/// </summary>
internal readonly record struct RendererPreset(
    string Name,
    float Scale,
    int SlotsPerFrame,
    bool HasReflections,
    int ReflectionStride
)
{
    public const string Names = "toaster, balanced, quality";

    public static readonly RendererPreset Toaster = new("toaster", 0.5f, 2, false, 2);

    public static readonly RendererPreset Balanced = new("balanced", 0.75f, 4, true, 2);

    public static readonly RendererPreset Quality = new("quality", 1, 8, true, 1);

    /// <summary>
    /// Every preset, from the least done a frame to the most.
    /// </summary>
    public static ImmutableArray<RendererPreset> All { get; } = [Toaster, Balanced, Quality];

    public static bool TryParse(string name, out RendererPreset preset)
    {
        foreach (RendererPreset known in All)
        {
            if (known.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                preset = known;

                return true;
            }
        }

        preset = default;

        return false;
    }
}
