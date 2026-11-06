using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The material table: a row per material drawn, made from the asset's params as it arrives and made again as the
/// textures it names arrive; row 0 the default a material is drawn with until then. A material that does not load,
/// names the failure shader or a texture that does not load is drawn as the failure kind. The whole table is
/// uploaded again in the frame after a row changes.
/// </summary>
internal sealed class MaterialTable(
    IGraphicsApi gpu,
    RenderGraph graph,
    Assets assets,
    TextureTable textures,
    Threads threads
) : IDisposable
{
    public const int Capacity = 4096;

    private const string FailureShader = "Shaders/Material/Failure.slang";

    private readonly GpuMaterial[] _rows = new GpuMaterial[Capacity];
    private readonly Material?[] _assets = new Material?[Capacity];
    private readonly Dictionary<ulong, uint> _rowOf = [];
    private readonly HashSet<uint> _waiting = [];
    private int _count = 1;
    private bool _isDirty = true;

    public GpuBuffer Buffer { get; private set; }

    public void Dispose()
    {
        if (!Buffer.IsNone)
            gpu.Destroy(Buffer);
    }

    public Result Create()
    {
        _rows[0] = GetDefault();
        Result<GpuBuffer> buffer = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)Capacity * GpuMaterial.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Material table",
            }
        );
        if (!buffer.Ok)
            return Result.Failure($"the material table was not made: {buffer.Error}");

        Buffer = buffer.Value;

        return Result.Success();
    }

    /// <summary>
    /// The material's row, 0 for none; a material first seen gets a row and its asset is read into it.
    /// </summary>
    public uint GetRow(Handle<Material> material)
    {
        if (material == Handle<Material>.None)
            return 0;

        if (_rowOf.TryGetValue(material.Id, out uint row))
            return row;

        if (_count >= Capacity)
        {
            Log.Error($"{assets.GetPath(material)} is drawn as the default material: the material table is full.");
            _rowOf[material.Id] = 0;

            return 0;
        }

        row = (uint)_count++;
        _rows[row] = GetDefault();
        _rowOf[material.Id] = row;
        LoadAsync(material, row).FireAndForget();

        return row;
    }

    /// <summary>
    /// Whether the material's row is made from its asset and the textures it names, or as the failure kind: what it is
    /// drawn as from the next upload pass on.
    /// </summary>
    public bool HasArrived(Handle<Material> material) =>
        _rowOf.TryGetValue(material.Id, out uint row)
        && (row == 0 || (_assets[row] is not null && !_waiting.Contains(row)));

    /// <summary>
    /// Makes again the rows whose textures were loading, once a frame.
    /// </summary>
    public void Refresh()
    {
        if (_waiting.Count == 0)
            return;

        uint[] waiting = [.. _waiting];
        foreach (uint row in waiting)
        {
            Rebuild(row);
        }
    }

    /// <summary>
    /// Copies the rows into the table, in the frame's upload pass, when any changed.
    /// </summary>
    public void Upload()
    {
        if (!_isDirty)
            return;

        _isDirty = false;
        int bytes = _count * GpuMaterial.Bytes;
        GpuUpload upload = gpu.BeginUpload(bytes);
        MemoryMarshal.AsBytes(_rows.AsSpan(0, _count)).CopyTo(upload.Bytes);
        graph.Use(Buffer, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Buffer, 0, (ulong)bytes);
    }

    private static GpuMaterial GetDefault() => new() { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1), Roughness = 0.6f };

    private static GpuMaterial GetFailure() =>
        new()
        {
            BaseColor = new Vector4(1, 0, 1, 1),
            Roughness = 1,
            Kind = (uint)MaterialKind.Failure,
        };

    /// <summary>
    /// The row a material's params make: "color", "roughness", "metalness", "emissive" and the "colorMap" texture,
    /// each the default when left out; the failure row when a texture failed, and the default while one loads.
    /// </summary>
    private void Rebuild(uint row)
    {
        if (_assets[row] is not { } material)
            return;

        Shading shading = material.Shading;
        GpuMaterial made = shading.Shader == assets.Find<Shader>(FailureShader) ? GetFailure() : GetDefault();
        MaterialParamDictionary parameters = shading.Params;
        if (parameters.TryGetValue("color", out MaterialParam? color))
            made.BaseColor = new Vector4(color.R, color.G, color.B, color.A);

        if (parameters.TryGetValue("roughness", out MaterialParam? roughness))
            made.Roughness = roughness.X;

        if (parameters.TryGetValue("metalness", out MaterialParam? metalness))
            made.Metalness = metalness.X;

        if (parameters.TryGetValue("emissive", out MaterialParam? emissive))
            made.Emissive = new Vector3(emissive.R, emissive.G, emissive.B);

        bool isWaiting = false;
        if (parameters.TryGetValue("colorMap", out MaterialParam? colorMap) && colorMap.Texture != Handle<Texture>.None)
        {
            switch (textures.Get(colorMap.Texture, out uint texture, out uint sampler))
            {
                case TextureState.Ready:
                    made.BaseColorTexture = texture;
                    made.BaseColorSampler = sampler;
                    break;
                case TextureState.Failed:
                    made = GetFailure();
                    break;
                case TextureState.Loading:
                    isWaiting = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(row), "not a texture state");
            }
        }

        if (isWaiting)
            _waiting.Add(row);
        else
            _waiting.Remove(row);

        if (_rows[row] == made)
            return;

        _rows[row] = made;
        _isDirty = true;
    }

    private async Task LoadAsync(Handle<Material> material, uint row)
    {
        Result<Material> loaded = await assets.LoadAsync(material, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!loaded.Ok)
        {
            Log.Error($"{assets.GetPath(material)} is drawn as a failure: {loaded.Error}");
            _assets[row] = new Material { Shading = new Shading { Shader = assets.Find<Shader>(FailureShader) } };
            Rebuild(row);

            return;
        }

        _assets[row] = loaded.Value;
        Rebuild(row);
    }
}
