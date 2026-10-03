using Magic.Attributes;
using Magic.Utils;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Magic.Contexts.Assets;

/// <summary>
/// What a <see cref="Components.Renderer"/> draws: the meshes of one source file and, per part, which of
/// the renderer's material slots it is drawn with. Meshes belong to the model instead of being assets of
/// their own because one file imports as one unit and a mesh is never referenced without its model.
/// <para>
/// A model also has a file form of its own (<c>.model</c>): its meshes, parts and slot names as they are in memory, so
/// a model made at run time (a generated LOD) is written with <see cref="ToBytes"/> and read back, by whichever gem
/// loads models, with <see cref="Read"/>, with nothing imported or converted in between.
/// </para>
/// </summary>
public sealed class Model : Asset
{
    public Mesh[] Meshes { get; set; } = [];

    public ModelPart[] Parts { get; set; } = [];

    /// <summary>
    /// Material name per slot, as the source file called them, so a scene can bind slots by name.
    /// </summary>
    public string[] SlotNames { get; set; } = [];

    /// <summary>
    /// The point of the model, in its own space, that sits at its entity's position, so scale and rotation turn
    /// about it: a bar whose origin is its left edge grows to the right. From the sidecar (<c>"origin"</c>); a
    /// script may set it, which every renderer of the model follows from when the model is next placed for drawing
    /// (nothing was drawing it), since the model is one asset shared by all of them.
    /// </summary>
    [MetaData]
    public Vector3 Origin { get; set; }

    public override long Bytes => Meshes.Sum(mesh => (mesh.Vertices.LongLength * Vertex.Size) + (mesh.Indices.LongLength * sizeof(uint)));

    private static ReadOnlySpan<byte> Signature => "MAGICMODEL1\n"u8;

    /// <summary>
    /// Whether <paramref name="bytes"/> are a model in its own file form, as <see cref="ToBytes"/> writes it.
    /// </summary>
    public static bool IsNative(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith(Signature);
    }

    /// <summary>
    /// The model in its own file form.
    /// </summary>
    public byte[] ToBytes()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8);
        writer.Write(Signature);
        writer.Write(Meshes.Length);
        foreach (Mesh mesh in Meshes)
        {
            writer.Write(mesh.Vertices.Length);
            writer.Write(mesh.Indices.Length);
            writer.Write(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        }

        writer.Write(Parts.Length);
        foreach (ModelPart part in Parts)
        {
            writer.Write(part.MeshIndex);
            writer.Write(part.MaterialSlot);
        }

        writer.Write(SlotNames.Length);
        foreach (string name in SlotNames)
            writer.Write(name);

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// Fills the model from its own file form; a failure when <paramref name="bytes"/> are not that, or are cut short.
    /// </summary>
    public Result Read(byte[] bytes)
    {
        if (!IsNative(bytes))
            return Result.Failure("not a .model file.");

        try
        {
            using BinaryReader reader = new(new MemoryStream(bytes, Signature.Length, bytes.Length - Signature.Length), Encoding.UTF8);
            Mesh[] meshes = new Mesh[reader.ReadInt32()];
            for (int i = 0; i < meshes.Length; i++)
            {
                Vertex[] vertices = new Vertex[reader.ReadInt32()];
                uint[] indices = new uint[reader.ReadInt32()];
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
                meshes[i] = new Mesh { Vertices = vertices, Indices = indices };
                meshes[i].ComputeBounds();
            }

            ModelPart[] parts = new ModelPart[reader.ReadInt32()];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = new ModelPart(reader.ReadInt32(), reader.ReadInt32());

            string[] names = new string[reader.ReadInt32()];
            for (int i = 0; i < names.Length; i++)
                names[i] = reader.ReadString();

            Meshes = meshes;
            Parts = parts;
            SlotNames = names;

            return Result.Success();
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException or ArgumentException or IOException)
        {
            return Result.Failure($"the .model file is damaged: {ex.Message}");
        }
    }
}

/// <summary>
/// One drawn mesh of a model: an index into <see cref="Model.Meshes"/> and the material slot it uses.
/// </summary>
public readonly record struct ModelPart(int MeshIndex, int MaterialSlot);
