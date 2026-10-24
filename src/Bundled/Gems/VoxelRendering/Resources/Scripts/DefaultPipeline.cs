using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;

namespace Magic.VoxelRendering.Resources.Scripts;

/// <summary>
/// The engine's frame: the sky's light captured and the world's voxels brought up to date, then each view's world
/// culled in two phases and drawn into a visibility buffer, each pixel's surface resolved into the G-buffer, how much
/// the world hides it from the sky traced through the voxels, and lit by the directional lights and the point and spot
/// lights, each shadowed by a cone traced toward it, and by the sky; the sky drawn behind it, and the world's post
/// effects run in order. A project that wants another frame, or this one with its own lighting shader, copies this file
/// into its own assets, renames the class, and names its copy in the renderer's Pipeline setting.
/// </summary>
internal sealed class DefaultPipeline : IRenderPipeline
{
    private const GpuTextureUsage Target = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled;

    /// <summary>
    /// The shader that lights each pixel from the G-buffer and the sky: Shaders/Renderer/Lighting.slang.
    /// </summary>
    public Handle<Shader> Lighting { get; set; } = new(4017178266632588897);

    /// <summary>
    /// The shader that traces how much the world hides each pixel's surface from the sky:
    /// Shaders/Renderer/Occlusion.slang.
    /// </summary>
    public Handle<Shader> Occlusion { get; set; } = new(1434464839250017112);

    /// <summary>
    /// Whether the sky's light is hidden where the world hides it, as the voxels trace it; without it, only a
    /// material's own occlusion hides it.
    /// </summary>
    public bool IsOccluded { get; set; } = true;

    public void Record(IRendering rendering)
    {
        if (rendering.Views.Count == 0) return;

        SkyLight sky = rendering.CaptureSky(rendering.Views[0]);
        VoxelScene voxels = rendering.VoxelizeScene();
        foreach (RenderView view in rendering.Views)
        {
            GpuTexture depth = rendering.Texture(ViewTexture(view, GpuFormat.D32Float, GpuTextureUsage.DepthTarget));
            GpuTexture ids = rendering.Texture(ViewTexture(view, GpuFormat.R32Uint, Target));

            // What last frame's depth says is in sight, then what that hid, tested against what was just drawn.
            rendering.DrawVisibility(view, rendering.Cull(view, CullPhase.First), ids, depth, GpuContents.Clear);
            rendering.BuildDepthPyramid(view, depth);
            rendering.DrawVisibility(view, rendering.Cull(view, CullPhase.Second), ids, depth, GpuContents.Keep);
            rendering.BuildDepthPyramid(view, depth);

            GBuffer g = new(
                depth,
                rendering.Texture(ViewTexture(view, GpuFormat.Rgba8Srgb, Target)),
                rendering.Texture(ViewTexture(view, GpuFormat.Rgb10A2Unorm, Target)),
                rendering.Texture(ViewTexture(view, GpuFormat.Rgba8Unorm, Target)),
                rendering.Texture(ViewTexture(view, GpuFormat.Rgba16Float, Target)),
                rendering.Texture(ViewTexture(view, GpuFormat.Rg16Float, Target)));
            rendering.Resolve(view, rendering.Classify(view, ids), ids, g);

            LightList lights = rendering.CullLights(view);
            GpuTexture occlusion = IsOccluded ? TraceOcclusion(rendering, view, g, voxels) : GpuTexture.None;
            GpuTexture hdr = rendering.Texture(
                ViewTexture(view, GpuFormat.Rgba16Float, Target | GpuTextureUsage.ComputeWrite));
            rendering.Dispatch(
                view,
                Lighting,
                new LightingConstants(g, sky, voxels, lights, occlusion, IsOccluded ? 1u : 0u, hdr),
                GpuGroups.For(view.Size, 8),
                [
                    g.Depth, g.Albedo, g.Normal, g.Surface, g.Emissive, sky.Dfg, sky.Sh, sky.Specular, view.Exposure,
                    voxels.Opacity, voxels.Levels, lights.Lights, lights.Bins, lights.Tiles,
                    IsOccluded ? occlusion : g.Depth,
                ],
                [hdr, rendering.Failures]);
            rendering.DrawSky(view, depth, hdr);

            rendering.Output(view, rendering.ApplyPostEffects(view, hdr));
        }
    }

    /// <summary>
    /// How much the world hides each pixel's surface, traced through the voxels into a texture of the view's size.
    /// </summary>
    private GpuTexture TraceOcclusion(IRendering rendering, RenderView view, in GBuffer g, in VoxelScene voxels)
    {
        GpuTexture occlusion = rendering.Texture(
            ViewTexture(view, GpuFormat.Rgba8Unorm, GpuTextureUsage.ComputeWrite));
        rendering.Dispatch(
            view,
            Occlusion,
            new OcclusionConstants(g, voxels, occlusion),
            GpuGroups.For(view.Size, 8),
            [g.Depth, g.Normal, voxels.Opacity, voxels.Levels],
            [occlusion]);

        return occlusion;
    }

    /// <summary>
    /// A texture of the view's size, sampled as well as <paramref name="usage"/> says.
    /// </summary>
    private static GpuTextureDescription ViewTexture(RenderView view, GpuFormat format, GpuTextureUsage usage)
        => new(format, usage | GpuTextureUsage.Sampled, (uint)view.Size.Width, (uint)view.Size.Height);

    /// <summary>
    /// The lighting's constants after its header, as Lighting.slang's <c>LightingConstants</c>: the G-buffer, the sky's
    /// light, the voxels, the view's lights, the occlusion and whether to read it, and the image lit into.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LightingConstants(
        GBuffer GBuffer,
        SkyLight Sky,
        VoxelScene Voxels,
        LightList Lights,
        GpuTexture Occlusion,
        uint IsOccluded,
        GpuTexture Hdr);

    /// <summary>
    /// The occlusion's constants after its header, as Occlusion.slang's <c>OcclusionConstants</c>: the G-buffer, the
    /// voxels, and the texture traced into.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OcclusionConstants(GBuffer GBuffer, VoxelScene Voxels, GpuTexture Occlusion);
}
