using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The sky's shader type and its three tables for a view: the light the air lets through by height and direction, the
/// light it scatters again by height and sun, and the sky as seen from the camera's height.
/// </summary>
/// <remarks>
/// The sky's type is its struct that implements <c>ISky</c>, as the type arguments of an entry. A pass generic over
/// the sky is specialised on it where there are tables, else on
/// <see cref="GetOpenSky"/>; it has both built before a domain shows. The tables' ids are in the frame's buffer as
/// <see cref="GpuSkyTables"/>. They are <see cref="GpuTexture.None"/> where no entity has a sky.
/// </remarks>
public readonly record struct SkyTables(
    ImmutableArray<ShaderTypeArgument> SkyType,
    GpuTexture Transmittance,
    GpuTexture MultiScattering,
    GpuTexture View
)
{
    /// <summary>
    /// Gets the open sky, as the type arguments of an entry generic over the sky, for a frame without tables.
    /// </summary>
    public static ImmutableArray<ShaderTypeArgument> GetOpenSky(AssetManager assets) =>
        [new(assets.Find<Shader>("Shaders/Sky/ISky.slang"), "OpenSky")];

    /// <summary>
    /// Gets the type a pass generic over the sky is specialised on in a frame: the sky's where it made its tables, else
    /// <paramref name="openSky"/>.
    /// </summary>
    public static ImmutableArray<ShaderTypeArgument> GetShading(
        in SkyTables? tables,
        ImmutableArray<ShaderTypeArgument> openSky
    ) => tables is { Transmittance.IsNone: false } sky ? sky.SkyType : openSky;
}
