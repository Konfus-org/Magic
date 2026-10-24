using System.Runtime.InteropServices;
using Magic.Contexts.Utils;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// The shaders one compile reads, as Slang reads files: an ISlangFileSystem, a COM object made here, which hands Slang
/// each file it asks for from the sources loaded before the compile began, a shader asset's path under <c>Shaders/</c>,
/// and answers that no other file is there. Slang asks for a file by its path relative to the file that includes or
/// imports it before its path from the root, so a path is looked up as it comes, its <c>./</c> taken off, ignoring case
/// as the engine's paths compare. The object goes once neither Slang nor its creator holds it.
/// </summary>
internal static unsafe class SourceFileSystem
{
    /// <summary>
    /// The object's methods, in ISlangFileSystem's order: ISlangUnknown's three, ISlangCastable's castAs and its own
    /// loadFile. Made once and kept for the whole run.
    /// </summary>
    private static readonly nint* _table = CreateTable();

    /// <summary>
    /// A file system over <paramref name="sources"/>, by path, held once by its creator, who releases it
    /// (<see cref="Slang.Release"/>).
    /// </summary>
    internal static nint Create(IReadOnlyDictionary<string, byte[]> sources)
    {
        FileSystem* system = (FileSystem*)NativeMemory.Alloc((nuint)sizeof(FileSystem));
        system->Table = _table;
        system->References = 1;
        system->Sources = GCHandle.ToIntPtr(GCHandle.Alloc(sources));

        return (nint)system;
    }

    private static nint* CreateTable()
    {
        nint* table = (nint*)NativeMemory.Alloc(5, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<FileSystem*, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<FileSystem*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<FileSystem*, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged<FileSystem*, Guid*, nint>)&CastAs;
        table[4] = (nint)(delegate* unmanaged<FileSystem*, byte*, nint*, int>)&LoadFile;

        return table;
    }

    private static bool IsImplemented(Guid* id)
        => *id == Slang.UnknownInterface || *id == Slang.CastableInterface || *id == Slang.FileSystemInterface;

    [UnmanagedCallersOnly]
    private static int QueryInterface(FileSystem* system, Guid* id, nint* found)
    {
        if (!IsImplemented(id))
        {
            *found = 0;

            return Slang.NoInterface;
        }

        Interlocked.Increment(ref system->References);
        *found = (nint)system;

        return Slang.Ok;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(FileSystem* system) => (uint)Interlocked.Increment(ref system->References);

    [UnmanagedCallersOnly]
    private static uint Release(FileSystem* system)
    {
        int left = Interlocked.Decrement(ref system->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(system->Sources).Free();
            NativeMemory.Free(system);
        }

        return (uint)left;
    }

    /// <summary>
    /// ISlangCastable's castAs: the object as an interface it implements, not held again.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint CastAs(FileSystem* system, Guid* id) => IsImplemented(id) ? (nint)system : 0;

    /// <summary>
    /// ISlangFileSystem's loadFile: a blob of the file's bytes, or not found. Slang takes no empty blob, so an empty
    /// file is a line break, which reads the same.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int LoadFile(FileSystem* system, byte* path, nint* blob)
    {
        *blob = 0;
        string key = CString.Read(path).Replace('\\', '/');
        while (key.StartsWith("./", StringComparison.Ordinal))
        {
            key = key[2..];
        }

        if (GCHandle.FromIntPtr(system->Sources).Target is not IReadOnlyDictionary<string, byte[]> sources
            || !sources.TryGetValue(key, out byte[]? bytes))
        {
            return Slang.NotFound;
        }

        ReadOnlySpan<byte> text = bytes.Length > 0 ? bytes : "\n"u8;
        fixed (byte* start = text)
        {
            *blob = Slang.slang_createBlob(start, (nuint)text.Length);
        }

        return *blob != 0 ? Slang.Ok : Slang.NotFound;
    }

    /// <summary>
    /// The object Slang holds: its table first, as every COM object's, then how many hold it and the sources it reads.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileSystem
    {
        public nint* Table;
        public int References;
        public nint Sources;
    }
}
