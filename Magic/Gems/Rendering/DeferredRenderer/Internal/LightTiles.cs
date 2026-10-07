using Magic.Contexts.Rendering;

namespace DeferredRendererGem;

/// <summary>
/// How the lights of a view are binned, the same numbers for the CPU and the shaders (written into their header).
/// First into screen tiles of <see cref="LightTileSize"/> pixels a side (Lighting/LightCull.comp.hlsl), one row of
/// <see cref="LightTileWords"/> uints per tile, left to right then top to bottom: how many lights reach what was drawn in
/// the tile, the nearest and the farthest view depth drawn there (the bits of two floats), then the lights' rows. Then
/// every tile is cut along its depth into <see cref="LightSlices"/> clusters (Lighting/LightCluster.comp.hlsl),
/// <see cref="LightClusterWords"/> uints each, slice after slice behind their tile: the count, then the lights of the
/// tile that reach that slice. A pixel is lit by the lights of its cluster. A row more lights reach than fit keeps the
/// first ones and its count says how many reach it, so the lighting shows it as a failure. A tile's slices are even
/// steps of the logarithm of the view depth between the nearest and the farthest depth drawn in it, so every tile
/// spends all its slices on the stretch it shows. A pass that creates a tile buffer sizes its rows by these
/// (<c>"stride": 4096</c> is <see cref="LightTileWords"/> words).
/// </summary>
[Hlsl]
internal static class LightTiles
{
    public const uint LightTileSize = 32u;
    public const uint LightTileWords = 1024u;
    public const uint LightTileHeader = 3u;
    public const uint LightsPerTile = LightTileWords - LightTileHeader;
    public const uint LightSlices = 16u;
    public const uint LightClusterWords = 64u;
    public const uint LightsPerCluster = LightClusterWords - 1u;
}
