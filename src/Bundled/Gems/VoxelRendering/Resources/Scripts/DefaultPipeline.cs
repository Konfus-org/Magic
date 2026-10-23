using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;

namespace Magic.VoxelRendering.Resources.Scripts;

/// <summary>
/// The engine's frame: the sky's light captured, then each view's world culled in two phases and drawn into a
/// visibility buffer, each pixel's surface resolved into the G-buffer, lit by the key light, the other directional
/// lights and the sky, the sky drawn behind it, and the world's post effects run in order. A project that wants another
/// frame, or this one with its own lighting shader, copies this file into its own assets, renames the class, and names
/// its copy in the renderer's Pipeline setting.
/// </summary>
internal sealed class DefaultPipeline : IRenderPipeline
{
    private const GpuTextureUsage Target = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled;

    /// <summary>
    /// The shader that lights each pixel from the G-buffer and the sky: Shaders/Rendering/Lighting/Lighting.comp.hlsl.
    /// </summary>
    public Handle<Shader> Lighting { get; set; } = new(4017178266632588897);

    public void Record(IRendering rendering)
    {
        if (rendering.Views.Count == 0) return;

        SkyLight sky = rendering.CaptureSky(rendering.Views[0]);
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

            GpuTexture hdr = rendering.Texture(
                ViewTexture(view, GpuFormat.Rgba16Float, Target | GpuTextureUsage.ComputeWrite));
            rendering.Dispatch(
                Lighting,
                new LightingConstants(
                    view.Constants,
                    g.Depth,
                    g.Albedo,
                    g.Normal,
                    g.Surface,
                    g.Emissive,
                    sky.Dfg,
                    sky.Sampler,
                    sky.Sh,
                    sky.Specular,
                    view.Exposure,
                    hdr),
                GpuGroups.For(view.Size, 8),
                [g.Depth, g.Albedo, g.Normal, g.Surface, g.Emissive, sky.Dfg, sky.Sh, sky.Specular, view.Exposure],
                [hdr, rendering.Failures]);
            rendering.DrawSky(view, depth, hdr);

            GpuTexture image = hdr;
            foreach (IPostEffect effect in rendering.PostEffects)
            {
                image = effect.Record(rendering, view, image);
            }

            rendering.Output(view, image);
        }
    }

    /// <summary>
    /// A texture of the view's size, sampled as well as <paramref name="usage"/> says.
    /// </summary>
    private static GpuTextureDescription ViewTexture(RenderView view, GpuFormat format, GpuTextureUsage usage)
        => new(format, usage | GpuTextureUsage.Sampled, (uint)view.Size.Width, (uint)view.Size.Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LightingConstants(
        GpuBuffer View,
        GpuTexture Depth,
        GpuTexture Albedo,
        GpuTexture Normal,
        GpuTexture Surface,
        GpuTexture Emissive,
        GpuTexture Dfg,
        GpuSampler Sampler,
        GpuBuffer Sh,
        GpuTexture Specular,
        GpuBuffer Exposure,
        GpuTexture Hdr);
}
