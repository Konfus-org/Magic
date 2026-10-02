using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Contexts.Rendering;

/// <summary>What an instance needs to know about its material: its record slot and the pipeline class that draws it.</summary>
internal readonly record struct MaterialSlot(uint Slot, PipelineClass Class);

/// <summary>One material as packed: the loaded asset (null when it failed), its class, the failure kind (0 for none) and the textures it holds.</summary>
internal sealed record MaterialState(uint Slot, Material? Material, PipelineClass Class, uint Failure, Handle<Texture>[] Textures);

/// <summary>
/// Every material in use, id to slot, reference counted: its packed <see cref="GpuMaterial"/> on the GPU, by slot, each
/// one's state an immutable <see cref="MaterialState"/> replaced whole when it is packed again. Slot 0 is the default
/// surface with its defaults, for an entity with no material at all; slot <see cref="MeshFailureSlot"/> is the failure
/// surface saying the model did not load, for every instance drawn as the failure cube. <see cref="RecordBytes"/> mirrors
/// the GPU records: a slot's record is written when it is packed, and <see cref="Dirty"/> means the mirror goes up again.
/// </summary>
internal sealed class MaterialTable(IRendering gpu, ulong defaultSurface, ulong failureSurface) : RefCountTable<ulong, uint>
{
    /// <summary>The failure kinds of Surfaces/Failure.surf.hlsl a record carries.</summary>
    public const uint FailureShader = 1, FailureMissing = 2, FailureMesh = 3, FailureTexture = 4;

    /// <summary>The slot every instance whose model did not load draws with.</summary>
    public const uint MeshFailureSlot = 1;

    public ulong DefaultSurface { get; } = defaultSurface;

    public ulong FailureSurface { get; } = failureSurface;

    public GrowableBuffer Records { get; } = new(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 1024 * GpuMaterial.Size);

    public Slots<MaterialState> States { get; } = new();

    /// <summary>Every slot's record, one after the other; grows with <see cref="States"/>.</summary>
    public byte[] RecordBytes { get; private set; } = new byte[64 * GpuMaterial.Size];

    public bool Dirty { get; set; } = true;

    public PipelineClass PlaceholderClass => States[0]!.Class;

    public MaterialSlot SlotOf(uint slot)
    {
        return new MaterialSlot(slot, States[slot]!.Class);
    }

    /// <summary>The bytes of a slot's record in the mirror.</summary>
    public Span<byte> Record(uint slot)
    {
        int end = ((int)slot + 1) * GpuMaterial.Size;
        if (end > RecordBytes.Length)
        {
            byte[] grown = RecordBytes;
            Array.Resize(ref grown, Math.Max(end, RecordBytes.Length * 2));
            RecordBytes = grown;
        }

        return RecordBytes.AsSpan(end - GpuMaterial.Size, GpuMaterial.Size);
    }
}
