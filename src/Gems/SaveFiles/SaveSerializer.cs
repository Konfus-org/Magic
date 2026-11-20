using System.Buffers.Binary;
using System.Text.Json;
using Magic.Files;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.SaveFiles;

/// <summary>
/// The save serializer: a header, then the changes of the world as JSON.
/// </summary>
/// <remarks>
/// The header is the four bytes <c>MSAV</c> and the version as a little-endian 32-bit number. The JSON names the types
/// of its components, and holds only the components that differ from what the files of the domains make. A file whose
/// header does not say the version that this class reads is refused with the reason.
/// </remarks>
public sealed class SaveSerializer : ISaveSerializer
{
    /// <summary>
    /// The version that this class writes and reads. Change it when the JSON changes in a way that an older file does
    /// not fit.
    /// </summary>
    private const int Version = 1;

    private static readonly byte[] _magic = "MSAV"u8.ToArray();

    private static readonly int _headerSize = _magic.Length + sizeof(int);

    /// <inheritdoc/>
    public Result<byte[]> Write(SaveData data)
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, Json.Options);
            byte[] bytes = new byte[_headerSize + json.Length];
            _magic.CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(_magic.Length), Version);
            json.CopyTo(bytes, _headerSize);

            return Result.Success(bytes);
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc/>
    public Result<SaveData> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < _headerSize || !bytes[.._magic.Length].SequenceEqual(_magic))
            return Result.Failure("it is not a Magic save file: its header is wrong.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[_magic.Length..]);
        if (version != Version)
            return Result.Failure(
                $"it is a save of format version {version}, and this Magic reads only version {Version}."
            );

        return Json.Read<SaveData>(bytes[_headerSize..]);
    }
}
