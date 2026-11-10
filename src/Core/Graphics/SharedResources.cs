namespace Magic.Graphics;

/// <summary>
/// What gems share on the GPU, beside the library's shader structs that read it: a gem that asks the graph with one of
/// these gets the resource, and the heap id, every other gem asking with it gets. A picture-sized texture is asked for
/// with the picture's width and height, a half-sized one with half of each, at least 1.
/// </summary>
public static class SharedResources
{
    /// <summary>
    /// The frame's constants: the library's <c>FrameConstants</c>, then the rows that follow it.
    /// </summary>
    public static readonly BufferDescription Frame = GetBuffer("Frame constants", 1024);

    /// <summary>
    /// The models' instance rows, 248 bytes each.
    /// </summary>
    public static readonly BufferDescription Instances = GetBuffer("Instance table", (1L << 18) * 248);

    /// <summary>
    /// The vertex pool, 32 bytes a vertex, and the index pool.
    /// </summary>
    public static readonly BufferDescription Vertices = GetBuffer("Vertex pool", (1L << 22) * 32);

    public static readonly BufferDescription Indices = GetBuffer("Index pool", (1L << 24) * sizeof(uint));

    /// <summary>
    /// The mesh table a ray hit finds its triangles and material slot through, 8 bytes a mesh.
    /// </summary>
    public static readonly BufferDescription Meshes = GetBuffer("Mesh table", (1L << 16) * 8);

    /// <summary>
    /// Each material row as a ray hit reads it, 32 bytes an instance.
    /// </summary>
    public static readonly BufferDescription HitMaterials = GetBuffer("Hit materials", (1L << 18) * 32);

    /// <summary>
    /// The local lights, 64 bytes each.
    /// </summary>
    public static readonly BufferDescription Lights = GetBuffer("Light table", 1024 * 64);

    /// <summary>
    /// The sky's tables: its transmittance, multiple scattering and view.
    /// </summary>
    public static readonly TextureDescription SkyTransmittance = GetTexture("Sky transmittance", 256, 64);

    public static readonly TextureDescription SkyMultiScattering = GetTexture("Sky multi-scattering", 32, 32);

    public static readonly TextureDescription SkyView = GetTexture("Sky view", 192, 108);

    /// <summary>
    /// The view's depth and GBuffer at the picture's size.
    /// </summary>
    public static readonly TextureDescription Depth = GetTexture("Depth", 0, 0, GpuFormat.D32Sfloat) with
    {
        Usage = TextureUsages.DepthTarget | TextureUsages.Sampled,
    };

    public static readonly TextureDescription GBufferNormal = GetTexture("GBuffer normal", 0, 0, GpuFormat.R16G16Unorm);

    public static readonly TextureDescription GBufferSurface = GetTexture(
        "GBuffer surface",
        0,
        0,
        GpuFormat.R8G8B8A8Unorm
    );

    public static readonly TextureDescription Velocity = GetTexture("Velocity", 0, 0);

    private static BufferDescription GetBuffer(string name, long bytes) =>
        new()
        {
            Bytes = bytes,
            Usage = BufferUsages.Storage,
            Name = name,
        };

    private static TextureDescription GetTexture(
        string name,
        int width,
        int height,
        GpuFormat format = GpuFormat.R16G16B16A16Sfloat
    ) =>
        new()
        {
            Width = width,
            Height = height,
            Format = format,
            Usage = TextureUsages.Storage | TextureUsages.Sampled,
            Name = name,
        };
}
