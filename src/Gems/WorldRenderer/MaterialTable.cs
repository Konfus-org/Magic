using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The material table: a row per material drawn, made from the asset's params as it arrives, row 0 the default a
/// material is drawn with until then. The whole table is uploaded again in the frame after a row changes.
/// </summary>
internal sealed class MaterialTable(IGraphicsApi gpu, AssetDatabase assets, Threads threads) : IDisposable
{
    public const int Capacity = 4096;

    private readonly GpuMaterial[] _rows = new GpuMaterial[Capacity];
    private readonly Dictionary<ulong, uint> _rowOf = [];
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
    /// Copies the rows into the table, in the frame's upload pass, when any changed.
    /// </summary>
    public void Upload(RenderGraph graph)
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

    /// <summary>
    /// The row a material's params make: "color", "roughness", "metalness" and "emissive", each the default when
    /// left out.
    /// </summary>
    private static GpuMaterial ToRow(Material material)
    {
        MaterialParamDictionary parameters = material.Visuals.Params;
        GpuMaterial row = GetDefault();
        if (parameters.TryGetValue("color", out MaterialParam? color))
            row.BaseColor = new Vector4(color.R, color.G, color.B, color.A);

        if (parameters.TryGetValue("roughness", out MaterialParam? roughness))
            row.Roughness = roughness.X;

        if (parameters.TryGetValue("metalness", out MaterialParam? metalness))
            row.Metalness = metalness.X;

        if (parameters.TryGetValue("emissive", out MaterialParam? emissive))
            row.Emissive = new Vector3(emissive.R, emissive.G, emissive.B);

        return row;
    }

    private async Task LoadAsync(Handle<Material> material, uint row)
    {
        Result<Material> loaded = await assets.LoadAsync(material, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!loaded.Ok)
        {
            Log.Error($"{assets.GetPath(material)} is drawn as the default material: {loaded.Error}");

            return;
        }

        _rows[row] = ToRow(loaded.Value);
        _isDirty = true;
    }
}
