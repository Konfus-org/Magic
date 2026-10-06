namespace DeferredRendererGem;

/// <summary>
/// What the renderer shows in place of the lit scene (<see cref="DeferredSettings.DebugView"/>): one of the gbuffer's
/// parts, one input of the lighting, the GI's volumes, or which level of detail every instance is drawn at. The
/// numbers are the shaders' (Include/Frame.hlsli's DebugView*), which read it from the frame's flags.
/// </summary>
public enum DebugView : byte
{
    Normal = 0,
    Albedo = 1,
    Normals = 2,
    Roughness = 3,
    Metallic = 4,
    Emissive = 5,
    Depth = 6,
    SunShadow = 7,
    AmbientOcclusion = 8,
    BentNormals = 9,
    LightCount = 10,
    GiLight = 11,
    SkyVisibility = 12,
    VoxelAlbedo = 13,
    VoxelCoverage = 14,
    LevelOfDetail = 15,
}
