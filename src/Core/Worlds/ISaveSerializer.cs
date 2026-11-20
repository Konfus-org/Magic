using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// Writes a <see cref="SaveData"/> as bytes and reads it back. A gem provides it, one at most for a run.
/// </summary>
/// <remarks>
/// The bytes name the types of the components and hold no instance, because the gems and the code of the project
/// unload between two runs, and the next run reads what this run wrote. Without a provider, the world cannot be saved,
/// and a restart keeps no world state.
/// </remarks>
public interface ISaveSerializer
{
    /// <summary>
    /// <paramref name="data"/> as the bytes of a save, or why it cannot be written.
    /// </summary>
    /// <remarks>
    /// The components in <paramref name="data"/> refer to script state that the next save frees, so the write must
    /// finish before it returns. It runs on a worker.
    /// </remarks>
    Result<byte[]> Write(SaveData data);

    /// <summary>
    /// <paramref name="bytes"/> as a <see cref="SaveData"/>, or why it is not one: bytes that this serializer did not write,
    /// or that a version of it that this one does not read wrote.
    /// </summary>
    Result<SaveData> Read(ReadOnlySpan<byte> bytes);
}
