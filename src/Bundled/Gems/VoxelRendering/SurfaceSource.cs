using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// A surface or sky shader read to compose and to pack materials for: its name, its text with its params' defaults and
/// roles taken out (HLSL takes neither) and their loader from the materials' table put in after them, and their layout.
/// One whose text is no more than an include of another (<c>Default.surf.hlsl</c>) is that one.
/// </summary>
internal sealed partial record SurfaceSource(string Name, string Text, ParamLayout Layout)
{
    /// <summary>
    /// It is a sky (<c>.sky.hlsl</c>), which draws the world's sky and nothing else, rather than a surface.
    /// </summary>
    internal bool IsSky => Name.EndsWith(".sky.hlsl", StringComparison.Ordinal);

    /// <summary>
    /// Reads <paramref name="surface"/>, or says why it cannot be drawn.
    /// </summary>
    internal static async Task<Result<SurfaceSource>> ReadAsync(
        Assets assets,
        Handle<Shader> surface,
        CancellationToken cancel)
    {
        Result<Shader> loaded = await assets.LoadAsync(surface, cancel).ConfigureAwait(false);

        if (!loaded.Ok) return Result.Failure<SurfaceSource>(loaded.Error);

        string name = assets.GetPath(surface) ?? $"surface {surface.Id}";
        string text = loaded.Value.Text;
        Result<ParamLayout?> layout = ParamLayout.Parse(text);

        if (!layout.Ok) return Result.Failure<SurfaceSource>($"{name}: {layout.Error}");

        // The materials' table is the view's, as the shader composed with the surface loaded it first (View.hlsli).
        if (layout.Value is { } parsed)
        {
            return Result.Success(new SurfaceSource(
                name,
                parsed.Prepare(
                    "LoadMaterialParams(uint material)",
                    "uint4 rows[8] = BUFFER(View.materials).Load<GpuMaterial>(material * 128u).rows;",
                    "rows"),
                parsed));
        }

        return IncludedSurface().Match(text) is { Success: true } include
            && assets.Find<Shader>("Shaders/" + include.Groups["path"].Value) is var included
            && included != surface
            ? await ReadAsync(assets, included, cancel).ConfigureAwait(false)
            : Result.Failure<SurfaceSource>($"{name} declares no struct MaterialParams, "
                + "which every surface and sky does, nor includes one that does.");
    }

    [GeneratedRegex(
        @"#\s*include\s+""(?<path>[^""]+\.(surf|sky)\.hlsl)""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IncludedSurface();
}
