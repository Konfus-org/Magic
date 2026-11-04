namespace WorldRenderer;

/// <summary>
/// How much the renderer does a frame, set with <c>set WorldRenderer.preset=&lt;name&gt;</c>: the share of the
/// output the picture is drawn at and resolved up from, the cards lit a frame, and whether the sharp reflections
/// are traced.
/// </summary>
internal readonly record struct RendererPreset(string Name, float Scale, int SlotsPerFrame, bool HasReflections)
{
    public const string Names = "toaster, balanced, quality";

    public static readonly RendererPreset Toaster = new("toaster", 0.5f, 2, false);

    public static readonly RendererPreset Balanced = new("balanced", 0.75f, 4, true);

    public static readonly RendererPreset Quality = new("quality", 1, 8, true);

    public static bool TryParse(string name, out RendererPreset preset)
    {
        foreach (RendererPreset known in (ReadOnlySpan<RendererPreset>)[Toaster, Balanced, Quality])
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
