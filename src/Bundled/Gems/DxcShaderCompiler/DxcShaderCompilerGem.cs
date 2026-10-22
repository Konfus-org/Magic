using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// <see cref="IShaderCompiler"/> on Microsoft's DirectXShaderCompiler (DXC), called directly so every option is the
/// engine's: HLSL 2021, Shader Model 6.0, warnings as errors, and SPIR-V for Vulkan 1.1, whose subgroup operations are
/// HLSL's wave intrinsics. What a shader binds is read from its SPIR-V, by name (<see cref="SpirvBindings"/>). A
/// shader's includes are shader assets, spliced in before DXC sees it.
/// What it compiles is kept in the cache (<see cref="Caches"/>, which it writes for as <see cref="ICache{T}"/> of
/// <see cref="GpuShader"/>): named by its source with its includes spliced in, its name, its stage and its format, so
/// no two sources share an entry, and made with the debug-info setting, so a shader compiles once, and again only when
/// it, an include or the setting changes. Each call makes its own DXC compiler, so it is safe from any thread; a shader
/// asked for while it already compiles waits for that compile rather than starting its own, so each compiles, and its
/// entry is written, once.
/// </summary>
internal sealed partial class DxcShaderCompilerGem(Assets assets, Caches caches, DxcShaderCompilerSettings settings) :
    IGem,
    IShaderCompiler,
    ICache<GpuShader>
{
    /// <summary>
    /// The text encoding DXC is told the source is in.
    /// </summary>
    private const uint Utf8CodePage = 65001;

    private static readonly Guid _compilerClass = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
    private static readonly Guid _compilerInterface = new("228b4687-5a6a-4730-900c-9702b2203f54");
    private static readonly Guid _resultInterface = new("58346cda-dde7-4497-9461-6f87af5e0659");

    private readonly Assets _assets = assets;
    private readonly Caches _caches = caches;
    private readonly DxcShaderCompilerSettings _settings = settings;

    /// <summary>
    /// Whether the shaders were last compiled with debug info, so a change while running compiles them again.
    /// </summary>
    private bool _debugInfo = settings.DebugInfo;

    /// <summary>
    /// The compiles on their way, by cache key, for a shader asked for again while it compiles to wait for.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, Task<Result<GpuShader>>> _compiling = new();

    /// <summary>
    /// Raised whenever DXC's arguments change, or how a shader is written to the cache, so what was compiled before is
    /// compiled again.
    /// </summary>
    public int Version => 4;

    /// <summary>
    /// Compiles every shader again when debug info was turned on or off while running.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (_settings.DebugInfo == _debugInfo) return;

        _debugInfo = _settings.DebugInfo;
        _assets.RemakeAsync<Shader>().FireAndForget();
    }

    /// <summary>
    /// The code's length, then what <see cref="GpuShader"/> declares in its order, its bindings each as name, kind and
    /// register, the code's bytes last.
    /// </summary>
    public void Write(GpuShader value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Code.Length);
        writer.Write(value.EntryPoint);
        writer.Write((int)value.Stage);
        writer.Write(value.ThreadCountX);
        writer.Write(value.ThreadCountY);
        writer.Write(value.ThreadCountZ);
        writer.Write(value.Bindings.Count);
        foreach (GpuShaderBinding binding in value.Bindings)
        {
            writer.Write(binding.Name);
            writer.Write((byte)binding.Kind);
            writer.Write(binding.Register);
        }

        writer.Write(value.Code.Span);
    }

    public Result<GpuShader> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        int length = reader.ReadInt32();
        string entryPoint = reader.ReadString();
        GpuStage stage = (GpuStage)reader.ReadInt32();
        uint threadsX = reader.ReadUInt32();
        uint threadsY = reader.ReadUInt32();
        uint threadsZ = reader.ReadUInt32();
        int count = reader.ReadInt32();
        List<GpuShaderBinding> bindings = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString();
            GpuBindingKind kind = (GpuBindingKind)reader.ReadByte();
            bindings.Add(new GpuShaderBinding(name, kind, reader.ReadUInt32()));
        }

        GpuShader shader = new()
        {
            EntryPoint = entryPoint,
            Stage = stage,
            Bindings = bindings,
            ThreadCountX = threadsX,
            ThreadCountY = threadsY,
            ThreadCountZ = threadsZ,
            Code = reader.ReadBytes(length),
        };

        return shader.Code.Length == length
            ? Result.Success(shader)
            : Result.Failure<GpuShader>("its code is cut short.");
    }

    public async Task<Result<GpuShader>> CompileAsync(
        string path,
        GpuStage stage,
        GpuShaderFormat format,
        CancellationToken cancel)
    {
        Handle<Shader> asset = _assets.Find<Shader>(path);

        if (asset == Handle<Shader>.None) return Result.Failure<GpuShader>($"{path}: no asset is there.");

        Result<Shader> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);

        return loaded.Ok
            ? await CompileAsync(loaded.Value.Text, path, stage, format, cancel).ConfigureAwait(false)
            : Result.Failure<GpuShader>(loaded.Error);
    }

    public async Task<Result<GpuShader>> CompileAsync(
        string hlsl,
        string name,
        GpuStage stage,
        GpuShaderFormat format,
        CancellationToken cancel)
    {
        (string profile, string define) = stage switch
        {
            GpuStage.Vertex => ("vs_6_0", "STAGE_VERTEX"),
            GpuStage.Fragment => ("ps_6_0", "STAGE_FRAGMENT"),
            GpuStage.Compute => ("cs_6_0", "STAGE_COMPUTE"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "There is no such shader stage."),
        };

        HashSet<string> including = new(StringComparer.OrdinalIgnoreCase) { name };
        Result<string> spliced = await SpliceAsync(hlsl, name, including, cancel).ConfigureAwait(false);

        if (!spliced.Ok) return Result.Failure<GpuShader>(spliced.Error);

        // The stage's macro goes in front, and a #line so DXC counts the shader's own lines from 1 again. The cache key
        // is the source as compiled, with its name, stage and format; a compile of that key already on its way is
        // waited for rather than started again.
        string source = $"#define {define} 1\n#line 1 \"{name}\"\n{spliced.Value}";
        ulong key = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes($"{name}\n{stage}\n{format}\n{source}"));
        TaskCompletionSource<Result<GpuShader>> own = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<GpuShader>> compiling = _compiling.GetOrAdd(key, own.Task);

        if (compiling != own.Task) return await compiling.WaitAsync(cancel).ConfigureAwait(false);

        try
        {
            Result<GpuShader> made = await ReadOrCompileAsync(key, source, name, profile, stage, format).ConfigureAwait(
                false);
            own.SetResult(made);

            return made;
        }
        catch (Exception ex)
        {
            own.SetException(ex);

            throw;
        }
        finally
        {
            _compiling.TryRemove(new(key, own.Task));
        }
    }

    [GeneratedRegex(
        """^\s*#\s*include\s+"(?<path>[^"]+)"\s*(//.*)?$""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Include();

    /// <summary>
    /// A COM method of a DXC object: the function at <paramref name="slot"/> of its table.
    /// </summary>
    private static unsafe nint GetMethod(nint instance, int slot) => (*(nint**)instance)[slot];

    private static unsafe void Release(nint instance)
    {
        // The third method of every COM object is IUnknown's Release.
        if (instance != 0) ((delegate* unmanaged<nint, uint>)GetMethod(instance, 2))(instance);
    }

    /// <summary>
    /// What a DXC result method at <paramref name="slot"/> hands out: its object or its error text, IDxcBlobs both.
    /// </summary>
    private static unsafe nint GetOutput(nint result, int slot)
    {
        nint output;
        ((delegate* unmanaged<nint, nint*, int>)GetMethod(result, slot))(result, &output);

        return output;
    }

    /// <summary>
    /// The bytes an IDxcBlob holds.
    /// </summary>
    private static unsafe ReadOnlySpan<byte> GetBytes(nint blob)
    {
        // IDxcBlob's GetBufferPointer and GetBufferSize, its first two methods after IUnknown's three.
        void* start = ((delegate* unmanaged<nint, void*>)GetMethod(blob, 3))(blob);
        nuint size = ((delegate* unmanaged<nint, nuint>)GetMethod(blob, 4))(blob);

        return new ReadOnlySpan<byte>(start, (int)size);
    }

    /// <summary>
    /// A null-terminated copy of <paramref name="text"/> as the OS's <c>wchar_t</c>: UTF-16 on Windows, UTF-32
    /// elsewhere. Freed with <see cref="NativeMemory.Free"/>.
    /// </summary>
    private static unsafe nint ToWideString(string text)
    {
        Encoding encoding = OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF32;
        int terminator = OperatingSystem.IsWindows() ? 2 : 4;
        byte[] bytes = encoding.GetBytes(text);
        byte* copy = (byte*)NativeMemory.AllocZeroed((nuint)(bytes.Length + terminator));
        bytes.CopyTo(new Span<byte>(copy, bytes.Length));

        return (nint)copy;
    }

    /// <summary>
    /// What the cache holds of <paramref name="key"/>, made with the debug-info setting as it is now, else
    /// <paramref name="source"/> compiled and kept there. Not cancelled: a compile is short and what it makes is kept,
    /// and whoever else waits for it stops waiting on their own.
    /// </summary>
    private async Task<Result<GpuShader>> ReadOrCompileAsync(
        ulong key,
        string source,
        string name,
        string profile,
        GpuStage stage,
        GpuShaderFormat format)
    {
        // An entry made with debug info is another version than one made without, so changing the setting finds the
        // entries dirty.
        ulong version = _settings.DebugInfo ? 1UL : 0UL;

        if (await _caches.ReadAsync<GpuShader>(key, version, CancellationToken.None)
            .ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached;
        }

        Result<GpuShader> compiled = await Task.Run(() => Compile(source, name, profile, stage, format))
            .ConfigureAwait(false);
        if (compiled.Ok)
            await _caches.WriteAsync(key, version, compiled.Value, CancellationToken.None).ConfigureAwait(false);

        return compiled;
    }

    /// <summary>
    /// <paramref name="source"/> with each <c>#include "path"</c> line replaced by the shader asset at
    /// <c>Shaders/path</c>, spliced in turn, so DXC needs no folder to find includes in: a shader includes whatever
    /// shaders are assets, whichever root they are under. Every include goes in where it is written, as the
    /// preprocessor would read it (its guards then keep a second copy out), between <c>#line</c>s that keep DXC's
    /// errors naming the file and line they are in. A file in <paramref name="including"/>, which holds those being
    /// spliced, is not spliced into itself again; an include no asset is at is left as it is, for DXC to report, and
    /// one that does not load fails the compile. <paramref name="name"/> is what <paramref name="source"/> is called.
    /// Paths compare ignoring case, as the engine's do on every OS.
    /// </summary>
    private async Task<Result<string>> SpliceAsync(
        string source,
        string name,
        HashSet<string> including,
        CancellationToken cancel)
    {
        StringBuilder spliced = new(source.Length);
        int number = 0;
        foreach (string line in source.Split('\n'))
        {
            number++;
            string path = Include().Match(line) is { Success: true } include
                ? include.Groups["path"].Value
                : string.Empty;
            Handle<Shader> asset = path.Length == 0 || including.Contains(path)
                ? Handle<Shader>.None
                : _assets.Find<Shader>("Shaders/" + path);
            if (asset == Handle<Shader>.None)
            {
                spliced.Append(line).Append('\n');

                continue;
            }

            Result<Shader> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);

            if (!loaded.Ok) return Result.Failure<string>($"{name}:{number}: {loaded.Error}");

            including.Add(path);
            Result<string> included = await SpliceAsync(loaded.Value.Text, path, including, cancel).ConfigureAwait(
                false);
            including.Remove(path);

            if (!included.Ok) return included;

            spliced.Append(CultureInfo.InvariantCulture, $"#line 1 \"{path}\"\n")
                .Append(included.Value)
                .Append(CultureInfo.InvariantCulture, $"#line {number + 1} \"{name}\"\n");
        }

        return Result.Success(spliced.ToString());
    }

    /// <summary>
    /// <paramref name="source"/>, its includes spliced in and its stage defined, compiled by DXC for
    /// <paramref name="profile"/> into SPIR-V, the one <paramref name="format"/> there is, with what it binds, by name,
    /// read from it (<see cref="SpirvBindings"/>).
    /// </summary>
    private Result<GpuShader> Compile(
        string source,
        string name,
        string profile,
        GpuStage stage,
        GpuShaderFormat format)
    {
        Result<byte[]> spirv = CompileWithDxc(source, name, profile);

        if (!spirv.Ok) return Result.Failure<GpuShader>(spirv.Error);

        Result<GpuShader> reflected = SpirvBindings.Read(spirv.Value, stage);

        if (!reflected.Ok)
            return Result.Failure<GpuShader>($"{name}: what it binds could not be read: {reflected.Error}");

        return format == GpuShaderFormat.Spirv
            ? Result.Success(reflected.Value with { Code = spirv.Value, EntryPoint = "main" })
            : throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such shader format.");
    }

    /// <summary>
    /// One DXC compile of <paramref name="source"/> to SPIR-V; a failure carries DXC's own text.
    /// </summary>
    private unsafe Result<byte[]> CompileWithDxc(string source, string name, string profile)
    {
        // Entry point main, the stage's profile, HLSL 2021, warnings as errors, optimised, and matrices column-major.
        List<string> arguments = ["-E", "main", "-T", profile, "-HV", "2021", "-WX", "-O3", "-Zpc"];

        // A resource the shader does not use is dropped, so what is read from the SPIR-V is what the shader uses; its
        // interface is kept whole, so the stages' varyings line up.
        arguments.AddRange(
            ["-spirv", "-fspv-target-env=vulkan1.1", "-fspv-flatten-resource-arrays", "-fspv-preserve-interface"]);
        if (_settings.DebugInfo) arguments.Add("-fspv-debug=vulkan-with-source");

        byte[] text = Encoding.UTF8.GetBytes(source);
        nint[] wide = new nint[arguments.Count];
        nint compiler = 0;
        nint result = 0;
        nint output = 0;
        nint errors = 0;
        try
        {
            // DXC takes its arguments as the OS's wide strings, freed once it has compiled.
            for (int i = 0; i < wide.Length; i++)
            {
                wide[i] = ToWideString(arguments[i]);
            }

            Guid compilerClass = _compilerClass;
            Guid compilerInterface = _compilerInterface;
            Guid resultInterface = _resultInterface;
            int created = NativeMethods.DxcCreateInstance(&compilerClass, &compilerInterface, &compiler);

            if (created < 0) return Result.Failure<byte[]>($"{name}: DXC could not start (0x{created:X8}).");

            // IDxcCompiler3's Compile, its first method after IUnknown's three.
            fixed (byte* bytes = text)
            fixed (nint* argumentList = wide)
            {
                DxcBuffer buffer = new() { Start = bytes, Size = (nuint)text.Length, Encoding = Utf8CodePage };
                nint compile = GetMethod(compiler, 3);
                int compiled = ((delegate* unmanaged<nint, DxcBuffer*, nint*, uint, nint, Guid*, nint*, int>)compile)(
                    compiler,
                    &buffer,
                    argumentList,
                    (uint)wide.Length,
                    0,
                    &resultInterface,
                    &result);

                if (compiled < 0) return Result.Failure<byte[]>($"{name}: DXC failed to run (0x{compiled:X8}).");
            }

            // The result's GetStatus, then its GetErrorBuffer when it failed: the first and third of its methods after
            // IUnknown's, GetResult, the SPIR-V, being the second.
            int status;
            ((delegate* unmanaged<nint, int*, int>)GetMethod(result, 3))(result, &status);
            if (status < 0)
            {
                errors = GetOutput(result, 5);
                string message = errors == 0
                    ? $"0x{status:X8}"
                    : Encoding.UTF8.GetString(GetBytes(errors)).TrimEnd('\0', '\n', ' ');

                return Result.Failure<byte[]>(message);
            }

            output = GetOutput(result, 4);

            if (output == 0) return Result.Failure<byte[]>($"{name}: DXC compiled nothing.");

            return Result.Success(GetBytes(output).ToArray());
        }
        finally
        {
            Release(errors);
            Release(output);
            Release(result);
            Release(compiler);
            foreach (nint argument in wide)
            {
                NativeMemory.Free((void*)argument);
            }
        }
    }

    /// <summary>
    /// DXC's <c>DxcBuffer</c>: source text and its encoding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxcBuffer
    {
        internal byte* Start;
        internal nuint Size;
        internal uint Encoding;
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("dxcompiler")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DxcCreateInstance(Guid* classId, Guid* interfaceId, nint* instance);
    }
}
