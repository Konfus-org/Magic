using Magic.Attributes;
using System.Numerics;

namespace Magic.Contexts.Assets;

/// <summary>How a material's surface is composited; it picks the pipeline the renderer draws it with.</summary>
public enum MaterialType : byte
{
    Opaque,

    /// <summary>Opaque, but fragments whose base colour alpha is below <see cref="Material.AlphaCutoff"/> are discarded.</summary>
    Masked,

    /// <summary>Blended over what is behind it, drawn after everything opaque.</summary>
    Transparent
}

/// <summary>
/// The inputs of the metallic-roughness PBR model, nothing else. The pipelines belong to the renderer,
/// because a GPU-driven renderer needs every opaque surface to go through the same few pipelines, and a
/// material can only pick data for them.
/// A <c>.mat</c> file is this class as JSON (<see cref="AssetJson"/>: vectors are <c>{x, y, z, w}</c>, textures
/// are <c>{ "id": N }</c>); its sidecar carries the <c>[MetaData]</c> properties. Every property has a default
/// so a file states only what differs. Texture handles are <see cref="Handle{T}.None"/> where the factor
/// alone applies; the ORM texture packs occlusion, roughness and metallic in r, g and b.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Material : Asset
{
    [MetaData]
    public MaterialType Type { get; set; }

    [MetaData]
    public bool DoubleSided { get; set; }

    public Vector4 Color { get; set; } = Vector4.One;

    public Vector3 Emissive { get; set; }

    public float Roughness { get; set; } = 1f;

    public float Metallic { get; set; }

    public float NormalScale { get; set; } = 1f;

    public float AlphaCutoff { get; set; } = 0.5f;

    public Handle<Texture> ColorTexture { get; set; }

    public Handle<Texture> NormalTexture { get; set; }

    public Handle<Texture> OrmTexture { get; set; }

    public Handle<Texture> EmissiveTexture { get; set; }
}
