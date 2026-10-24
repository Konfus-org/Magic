using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// How a view is exposed: for the light falling on the world where the camera is, as a light meter held there reads
/// it, so a thing looks the same however much of the view something darker or brighter fills; eased toward, and kept
/// between the darkest and brightest light it adapts to, in EV at ISO 100. A night darker than <see cref="MinEv"/>
/// stays dark, as eyes see it, rather than turning into a dim day as a long exposure would. It writes the view's
/// exposure (<see cref="RenderView.Exposure"/>), which the lighting and the sky read from the next frame on, and leaves
/// the image as it is.
/// </summary>
public readonly record struct Exposure(Handle<Shader> Shader, float MinEv = 0, float MaxEv = 0) : IPostEffect
{
    /// <summary>
    /// The brightest light the view adapts to, in EV; zero reads as 18, brighter than a sunlit snowfield.
    /// </summary>
    public float MaxEv { get => field == 0 ? 18 : field; init; } = MaxEv;

    public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
    {
        rendering.Dispatch(
            view,
            Shader,
            new MeterConstants(MinEv, MaxEv),
            new GpuGroups(1),
            [],
            [view.Exposure]);

        return image;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MeterConstants(float MinEv, float MaxEv);
}
