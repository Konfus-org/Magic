using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.GIRendering;

/// <summary>
/// The material table: a row per <see cref="ShadingInstance"/> drawn, made from its material's params with the
/// instance's over them as the material arrives and made again as the textures it names arrive; row 0 the default a
/// row is drawn with until then. A material that does not load, names the failure shader or a texture that does not
/// load is drawn as the failure kind. The whole table is uploaded again in the frame after a row changes.
/// </summary>
internal sealed class MaterialTable(
    IGraphicsApi gpu,
    RenderGraph graph,
    Assets assets,
    TextureTable textures,
    Threads threads
) : IDisposable
{
    /// <summary>
    /// As many rows as the instance table has, which holds every drawn slot of a scene whose renderers fill one each.
    /// </summary>
    public const int Capacity = InstanceTable.Capacity;

    private const string FailureShader = "Shaders/Material/Failure.slang";

    private readonly GpuMaterial[] _rows = new GpuMaterial[Capacity];
    private readonly ShadingInstance?[] _instances = new ShadingInstance?[Capacity];
    private readonly Dictionary<ShadingInstance, uint> _rowOf = [];

    /// <summary>
    /// Each material loaded, by its id, null while it loads; one that did not load is the failure material.
    /// </summary>
    private readonly Dictionary<ulong, Material?> _materials = [];

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
    /// The instance's row, 0 for none; an instance first seen gets a row, and its material is loaded if it is not yet.
    /// </summary>
    public uint GetRow(ShadingInstance? instance)
    {
        if (instance is null)
            return 0;

        if (_rowOf.TryGetValue(instance, out uint row))
            return row;

        if (_count >= Capacity)
        {
            Log.Error(
                $"{assets.GetPath(instance.Material)} is drawn as the default material: the material table is full."
            );
            _rowOf[instance] = 0;

            return 0;
        }

        row = (uint)_count++;
        _rows[row] = GetDefault();
        _instances[row] = instance;
        _rowOf[instance] = row;
        Load(instance.Material);
        Rebuild(row);

        return row;
    }

    /// <summary>
    /// Starts loading the material, once, for the rows of its instances to be made from.
    /// </summary>
    public void Load(Handle<Material> material)
    {
        if (_materials.ContainsKey(material.Id))
            return;

        _materials[material.Id] = null;
        LoadAsync(material).FireAndForget();
    }

    /// <summary>
    /// Whether the material and the textures it names are loaded, or it is drawn as the failure kind: what its rows are
    /// made from, from the next upload pass on.
    /// </summary>
    public bool HasArrived(Handle<Material> material) =>
        _materials.GetValueOrDefault(material.Id) is { } loaded
        && GetColorMap(loaded.Shading.Params, out _, out _) != TextureState.Loading;

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
    /// The state of the "colorMap" texture <paramref name="parameters"/> name, asking the texture table for it, with its
    /// places there once it is ready; ready, as nothing, when they name none.
    /// </summary>
    private TextureState GetColorMap(MaterialParamDictionary parameters, out uint texture, out uint sampler)
    {
        if (parameters.TryGetValue("colorMap", out MaterialParam? colorMap) && colorMap.Texture != Handle<Texture>.None)
            return textures.Get(colorMap.Texture, out texture, out sampler);

        texture = 0;
        sampler = 0;

        return TextureState.Ready;
    }

    /// <summary>
    /// The row an instance's params over its material's make: "color", "roughness", "metalness", "emissive" and the
    /// material's "colorMap" texture, each the default when left out; the failure row when the texture failed, and the
    /// default while the material or the texture loads.
    /// </summary>
    private void Rebuild(uint row)
    {
        if (_instances[row] is not { } instance)
            return;

        if (_materials.GetValueOrDefault(instance.Material.Id) is not { } material)
            return;

        Shading shading = material.Shading;
        GpuMaterial made = shading.Shader == assets.Find<Shader>(FailureShader) ? GetFailure() : GetDefault();
        if (TryGet("color", out MaterialParam? color))
            made.BaseColor = new Vector4(color.R, color.G, color.B, color.A);

        if (TryGet("roughness", out MaterialParam? roughness))
            made.Roughness = roughness.X;

        if (TryGet("metalness", out MaterialParam? metalness))
            made.Metalness = metalness.X;

        if (TryGet("emissive", out MaterialParam? emissive))
            made.Emissive = new Vector3(emissive.R, emissive.G, emissive.B);

        bool isWaiting = false;
        switch (GetColorMap(shading.Params, out uint texture, out uint sampler))
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

        if (isWaiting)
            _waiting.Add(row);
        else
            _waiting.Remove(row);

        if (_rows[row] == made)
            return;

        _rows[row] = made;
        _isDirty = true;

        bool TryGet(string name, [MaybeNullWhen(false)] out MaterialParam param) =>
            instance.Params.TryGetValue(name, out param) || shading.Params.TryGetValue(name, out param);
    }

    /// <summary>
    /// Makes again the rows of every instance of <paramref name="material"/>, as it arrived.
    /// </summary>
    private void RebuildRowsOf(Handle<Material> material)
    {
        for (uint row = 1; row < _count; row++)
        {
            if (_instances[row] is { } instance && instance.Material == material)
                Rebuild(row);
        }
    }

    private async Task LoadAsync(Handle<Material> material)
    {
        Result<Material> loaded = await assets.LoadAsync(material, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!loaded.Ok)
        {
            Log.Error($"{assets.GetPath(material)} is drawn as a failure: {loaded.Error}");
            _materials[material.Id] = new Material
            {
                Shading = new Shading { Shader = assets.Find<Shader>(FailureShader) },
            };
            RebuildRowsOf(material);

            return;
        }

        _materials[material.Id] = loaded.Value;
        // Asks the texture table for its texture now, so the loading gate waits for it before any row needs it.
        GetColorMap(loaded.Value.Shading.Params, out _, out _);
        RebuildRowsOf(material);
    }
}
