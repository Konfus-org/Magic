using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Rendering;

/// <summary>
/// A material as the GPU reads it from the material table, 48 bytes: the twin of <c>Scene/Materials.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuMaterial
{
    public const int Bytes = 48;

    public Vector4 BaseColor;

    public Vector3 Emissive;

    public float Roughness;

    public float Metalness;

    /// <summary>
    /// The heap ids of the base colour texture and the sampler it is read with; 0 for none.
    /// </summary>
    public uint BaseColorTexture;

    public uint BaseColorSampler;

    /// <summary>
    /// Which material contract the row is drawn with: a <see cref="MaterialKind"/>.
    /// </summary>
    public uint Kind;
}

/// <summary>
/// Which material contract a <see cref="GpuMaterial"/> row is drawn with.
/// </summary>
public enum MaterialKind : uint
{
    Standard,

    /// <summary>
    /// The magenta a broken material, shader or texture is drawn as.
    /// </summary>
    Failure,

    /// <summary>
    /// Drawn after the lighting, forward, letting the picture behind through its colour.
    /// </summary>
    Glass,
}
