using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using SDL;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// <see cref="IShaderCompiler"/> on Microsoft's DirectXShaderCompiler (DXC), called directly so every option is the
/// engine's: HLSL 2021, Shader Model 6.0, warnings as errors, and SPIR-V for Vulkan 1.1, whose subgroup operations are
/// HLSL's wave intrinsics. Every shader is compiled to SPIR-V first, whatever the format asked for, since what it binds
/// is read from that (SDL_shadercross's reflection); DXIL is compiled from the HLSL again, and Metal is translated from
/// the SPIR-V (SDL_shadercross, over SPIRV-Cross). A shader's includes are shader assets, spliced in before DXC sees it.
/// What it compiles is kept in the cache (<see cref="Caches"/>, which it writes for as <see cref="ICache{T}"/> of
/// <see cref="GpuShader"/>): named by its source with its includes spliced in, its name, its stage and its format, so
/// no two sources share an entry, and made with the debug-info setting, so a shader compiles once, and again only when
/// it, an include or the setting changes. Each call makes its own DXC compiler, so it is safe from any thread.
/// </summary>
internal sealed partial class DxcShaderCompilerGem : IGem, IShaderCompiler, ICache<GpuShader>, IDisposable
{
    /// <summary>
    /// Raised whenever DXC's arguments change, or how a shader is written to the cache, so what was compiled before is
    /// compiled again.
    /// </summary>
    private const int CacheVersion = 1;

    /// <summary>
    /// The text encoding DXC is told the source is in.
    /// </summary>
    private const uint Utf8CodePage = 65001;

    /// <summary>
    /// The Metal Shading Language translated to: 2.3 (macOS 11) has the SIMD-group functions wave intrinsics become.
    /// </summary>
    private const string MslVersion = "2.3.0";

    private static readonly Guid _compilerClass = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
    private static readonly Guid _compilerInterface = new("228b4687-5a6a-4730-900c-9702b2203f54");
    private static readonly Guid _resultInterface = new("58346cda-dde7-4497-9461-6f87af5e0659");

    private readonly Assets _assets;
    private readonly DxcShaderCompilerSettings _settings;
    private readonly Caches _caches;

    public DxcShaderCompilerGem(Assets assets, Caches caches, DxcShaderCompilerSettings settings)
    {
        _assets = assets;
        _settings = settings;
        _caches = caches;

        // SDL_shadercross links SDL, so SDL loads first: the copy every gem shares (GemLoadContext).
        int sdl = SDL3.SDL_GetVersion();
        Debugging.Verbose($"Shader compiler on SDL {sdl / 1000000}.{sdl / 1000 % 1000}.{sdl % 1000}");

        // DXC signs DXIL with dxil, which it loads by name from the OS's search path, where this gem's folder is not:
        // loaded here first, it is found already in the process. Unsigned DXIL is refused by Direct3D 12.
        if (OperatingSystem.IsWindows() && !NativeLibrary.TryLoad("dxil", typeof(DxcShaderCompilerGem).Assembly, null, out _))
        {
            Debugging.Warn("dxil was not found, so DXIL shaders are not signed and Direct3D 12 will refuse them.");
        }

        if (!NativeMethods.Init())
        {
            throw new InvalidOperationException($"SDL_shadercross could not start: {SDL3.SDL_GetError()}");
        }
    }

    public void Dispose()
    {
        NativeMethods.Quit();
    }

    public int Version => CacheVersion;

    /// <summary>
    /// The code's length, then what <see cref="GpuShader"/> declares in its order, the code's bytes last.
    /// </summary>
    public void Write(GpuShader value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Code.Length);
        writer.Write(value.EntryPoint);
        writer.Write((int)value.Stage);
        writer.Write(value.Samplers);
        writer.Write(value.StorageTextures);
        writer.Write(value.StorageBuffers);
        writer.Write(value.UniformBuffers);
        writer.Write(value.ReadWriteStorageTextures);
        writer.Write(value.ReadWriteStorageBuffers);
        writer.Write(value.ThreadCountX);
        writer.Write(value.ThreadCountY);
        writer.Write(value.ThreadCountZ);
        writer.Write(value.Code.Span);
    }

    public Result<GpuShader> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        int length = reader.ReadInt32();
        GpuShader shader = new()
        {
            EntryPoint = reader.ReadString(),
            Stage = (GpuStage)reader.ReadInt32(),
            Samplers = reader.ReadUInt32(),
            StorageTextures = reader.ReadUInt32(),
            StorageBuffers = reader.ReadUInt32(),
            UniformBuffers = reader.ReadUInt32(),
            ReadWriteStorageTextures = reader.ReadUInt32(),
            ReadWriteStorageBuffers = reader.ReadUInt32(),
            ThreadCountX = reader.ReadUInt32(),
            ThreadCountY = reader.ReadUInt32(),
            ThreadCountZ = reader.ReadUInt32(),
            Code = reader.ReadBytes(length),
        };
        return shader.Code.Length == length ? Result.Success(shader) : Result.Failure<GpuShader>("its code is cut short.");
    }

    public async Task<Result<GpuShader>> CompileAsync(string hlsl, string name, GpuStage stage, GpuShaderFormat format, CancellationToken cancel)
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
        if (!spliced.Ok)
        {
            return Result.Failure<GpuShader>(spliced.Error);
        }

        string source = $"#define {define} 1\n#line 1 \"{name}\"\n{spliced.Value}";
        ulong key = Hash($"{name}\n{stage}\n{format}\n{source}");
        ulong version = _settings.DebugInfo ? 1UL : 0UL;
        if (await _caches.ReadAsync<GpuShader>(key, version, cancel).ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached;
        }

        Result<GpuShader> compiled = await Task.Run(() => Compile(source, name, profile, stage, format), cancel).ConfigureAwait(false);
        if (compiled.Ok)
        {
            await _caches.WriteAsync(key, version, compiled.Value, cancel).ConfigureAwait(false);
        }

        return compiled;
    }

    /// <summary>
    /// <paramref name="source"/> with each <c>#include "path"</c> line replaced by the shader asset at
    /// <c>Shaders/path</c>, spliced in turn, so DXC needs no folder to find includes in: a shader includes whatever
    /// shaders are assets, whichever root they are under. Every include goes in where it is written, as the preprocessor
    /// would read it (its guards then keep a second copy out), between <c>#line</c>s that keep DXC's errors naming the
    /// file and line they are in. A file in <paramref name="including"/>, which holds those being spliced, is not spliced
    /// into itself again; an include no asset is at is left as it is, for DXC to report, and one that does not load fails
    /// the compile. <paramref name="name"/> is what <paramref name="source"/> is called. Paths compare ignoring case, as
    /// the engine's do on every OS.
    /// </summary>
    private async Task<Result<string>> SpliceAsync(string source, string name, HashSet<string> including, CancellationToken cancel)
    {
        StringBuilder spliced = new(source.Length);
        int number = 0;
        foreach (string line in source.Split('\n'))
        {
            number++;
            string path = Include().Match(line) is { Success: true } include ? include.Groups["path"].Value : string.Empty;
            Handle<Shader> asset = path.Length == 0 || including.Contains(path) ? Handle<Shader>.None : _assets.Find<Shader>("Shaders/" + path);
            if (asset == Handle<Shader>.None)
            {
                spliced.Append(line).Append('\n');
                continue;
            }

            Result<Shader> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);
            if (!loaded.Ok)
            {
                return Result.Failure<string>($"{name}:{number}: {loaded.Error}");
            }

            including.Add(path);
            Result<string> included = await SpliceAsync(loaded.Value.Text, path, including, cancel).ConfigureAwait(false);
            including.Remove(path);
            if (!included.Ok)
            {
                return included;
            }

            spliced.Append(CultureInfo.InvariantCulture, $"#line 1 \"{path}\"\n")
                .Append(included.Value)
                .Append(CultureInfo.InvariantCulture, $"#line {number + 1} \"{name}\"\n");
        }

        return Result.Success(spliced.ToString());
    }

    /// <summary>
    /// <paramref name="source"/>, its includes spliced in and its stage defined, compiled by DXC for
    /// <paramref name="profile"/> into <paramref name="format"/>, with the counts of what it binds.
    /// </summary>
    private Result<GpuShader> Compile(string source, string name, string profile, GpuStage stage, GpuShaderFormat format)
    {
        Result<byte[]> spirv = Dxc(source, name, profile, isSpirv: true);
        if (!spirv.Ok)
        {
            return Result.Failure<GpuShader>(spirv.Error);
        }

        Result<GpuShader> reflected = Reflect(spirv.Value, name, stage);
        if (!reflected.Ok)
        {
            return reflected;
        }

        Result<byte[]> code = format switch
        {
            GpuShaderFormat.Spirv => spirv,
            GpuShaderFormat.Dxil => Dxc(source, name, profile, isSpirv: false),
            GpuShaderFormat.Msl => Msl(spirv.Value, name, stage),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no such shader format."),
        };
        return code.Ok
            ? Result.Success(reflected.Value with { Code = code.Value, EntryPoint = format == GpuShaderFormat.Msl ? "main0" : "main" })
            : Result.Failure<GpuShader>(code.Error);
    }

    /// <summary>
    /// What a SPIR-V shader binds, counted as SDL GPU and the engine's binding model count it.
    /// </summary>
    private static unsafe Result<GpuShader> Reflect(byte[] spirv, string name, GpuStage stage)
    {
        fixed (byte* code = spirv)
        {
            if (stage == GpuStage.Compute)
            {
                ComputeResources* compute = NativeMethods.ReflectComputeSpirv(code, (nuint)spirv.Length, 0);
                if (compute == null)
                {
                    return Result.Failure<GpuShader>($"{name}: what it binds could not be read: {SDL3.SDL_GetError()}");
                }

                GpuShader computeShader = new()
                {
                    Code = spirv,
                    EntryPoint = "main",
                    Stage = stage,
                    Samplers = compute->Samplers,
                    StorageTextures = compute->StorageTextures,
                    StorageBuffers = compute->StorageBuffers,
                    ReadWriteStorageTextures = compute->ReadWriteStorageTextures,
                    ReadWriteStorageBuffers = compute->ReadWriteStorageBuffers,
                    UniformBuffers = compute->UniformBuffers,
                    ThreadCountX = compute->ThreadCountX,
                    ThreadCountY = compute->ThreadCountY,
                    ThreadCountZ = compute->ThreadCountZ,
                };
                SDL3.SDL_free(compute);
                return Result.Success(computeShader);
            }

            GraphicsResources* graphics = NativeMethods.ReflectGraphicsSpirv(code, (nuint)spirv.Length, 0);
            if (graphics == null)
            {
                return Result.Failure<GpuShader>($"{name}: what it binds could not be read: {SDL3.SDL_GetError()}");
            }

            GpuShader shader = new()
            {
                Code = spirv,
                EntryPoint = "main",
                Stage = stage,
                Samplers = graphics->Samplers,
                StorageTextures = graphics->StorageTextures,
                StorageBuffers = graphics->StorageBuffers,
                UniformBuffers = graphics->UniformBuffers,
            };
            SDL3.SDL_free(graphics);
            return Result.Success(shader);
        }
    }

    /// <summary>
    /// SPIR-V translated to Metal Shading Language source, its entry point renamed <c>main0</c>.
    /// </summary>
    private static unsafe Result<byte[]> Msl(byte[] spirv, string name, GpuStage stage)
    {
        SDL_PropertiesID properties = SDL3.SDL_CreateProperties();
        SDL3.SDL_SetStringProperty(properties, "SDL_shadercross.spirv.msl.version", MslVersion);
        try
        {
            fixed (byte* code = spirv)
            fixed (byte* entry = "main"u8)
            {
                SpirvInfo info = new() { Bytecode = code, BytecodeSize = (nuint)spirv.Length, EntryPoint = entry, Stage = (int)stage, Properties = (uint)properties };
                byte* msl = NativeMethods.TranspileMslFromSpirv(&info);
                if (msl == null)
                {
                    return Result.Failure<byte[]>($"{name}: SPIR-V could not be translated to Metal: {SDL3.SDL_GetError()}");
                }

                byte[] text = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(msl).ToArray();
                SDL3.SDL_free(msl);
                return Result.Success(text);
            }
        }
        finally
        {
            SDL3.SDL_DestroyProperties(properties);
        }
    }

    [GeneratedRegex("""^\s*#\s*include\s+"(?<path>[^"]+)"\s*(//.*)?$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Include();

    /// <summary>
    /// A COM method of a DXC object: the function at <paramref name="slot"/> of its table.
    /// </summary>
    private static unsafe nint Method(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged<nint, uint>)Method(instance, 2))(instance);
        }
    }

    /// <summary>
    /// What a DXC result method at <paramref name="slot"/> hands out: its object or its error text, IDxcBlobs both.
    /// </summary>
    private static unsafe nint Output(nint result, int slot)
    {
        nint output;
        ((delegate* unmanaged<nint, nint*, int>)Method(result, slot))(result, &output);
        return output;
    }

    /// <summary>
    /// The bytes an IDxcBlob holds.
    /// </summary>
    private static unsafe ReadOnlySpan<byte> Bytes(nint blob)
    {
        void* start = ((delegate* unmanaged<nint, void*>)Method(blob, 3))(blob);
        nuint size = ((delegate* unmanaged<nint, nuint>)Method(blob, 4))(blob);
        return new ReadOnlySpan<byte>(start, (int)size);
    }

    /// <summary>
    /// A null-terminated copy of <paramref name="text"/> as the OS's <c>wchar_t</c>: UTF-16 on Windows, UTF-32
    /// elsewhere. Freed with <see cref="NativeMemory.Free"/>.
    /// </summary>
    private static unsafe nint WideString(string text)
    {
        Encoding encoding = OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF32;
        int terminator = OperatingSystem.IsWindows() ? 2 : 4;
        byte[] bytes = encoding.GetBytes(text);
        byte* copy = (byte*)NativeMemory.AllocZeroed((nuint)(bytes.Length + terminator));
        bytes.CopyTo(new Span<byte>(copy, bytes.Length));
        return (nint)copy;
    }

    /// <summary>
    /// One DXC compile of <paramref name="source"/>, to SPIR-V or DXIL; a failure carries DXC's own text.
    /// </summary>
    private unsafe Result<byte[]> Dxc(string source, string name, string profile, bool isSpirv)
    {
        List<string> arguments = ["-E", "main", "-T", profile, "-HV", "2021", "-WX", "-O3", "-Zpc"];

        // Vulkan's attributes ([[vk::image_format]]) mean nothing to DXIL, which warns that it ignores them; the SPIR-V
        // compile, which always runs first, still checks every one with every warning on.
        arguments.AddRange(isSpirv
            ? ["-spirv", "-fspv-target-env=vulkan1.1", "-fspv-flatten-resource-arrays", "-fspv-preserve-bindings", "-fspv-preserve-interface"]
            : ["-Wno-ignored-attributes"]);

        if (_settings.DebugInfo)
        {
            arguments.AddRange(isSpirv ? ["-fspv-debug=vulkan-with-source"] : ["-Zi", "-Qembed_debug"]);
        }

        byte[] text = Encoding.UTF8.GetBytes(source);
        nint[] wide = new nint[arguments.Count];
        nint compiler = 0;
        nint result = 0;
        nint output = 0;
        nint errors = 0;
        try
        {
            for (int i = 0; i < wide.Length; i++)
            {
                wide[i] = WideString(arguments[i]);
            }

            Guid compilerClass = _compilerClass;
            Guid compilerInterface = _compilerInterface;
            Guid resultInterface = _resultInterface;
            int created = NativeMethods.DxcCreateInstance(&compilerClass, &compilerInterface, &compiler);
            if (created < 0)
            {
                return Result.Failure<byte[]>($"{name}: DXC could not start (0x{created:X8}).");
            }

            fixed (byte* bytes = text)
            fixed (nint* argumentList = wide)
            {
                DxcBuffer buffer = new() { Start = bytes, Size = (nuint)text.Length, Encoding = Utf8CodePage };
                int compiled = ((delegate* unmanaged<nint, DxcBuffer*, nint*, uint, nint, Guid*, nint*, int>)Method(compiler, 3))(compiler, &buffer, argumentList, (uint)wide.Length, 0, &resultInterface, &result);
                if (compiled < 0)
                {
                    return Result.Failure<byte[]>($"{name}: DXC failed to run (0x{compiled:X8}).");
                }
            }

            int status;
            ((delegate* unmanaged<nint, int*, int>)Method(result, 3))(result, &status);
            if (status < 0)
            {
                errors = Output(result, 5);
                string message = errors == 0 ? $"0x{status:X8}" : Encoding.UTF8.GetString(Bytes(errors)).TrimEnd('\0', '\n', ' ');
                return Result.Failure<byte[]>(message);
            }

            output = Output(result, 4);
            return output == 0 ? Result.Failure<byte[]>($"{name}: DXC compiled nothing.") : Result.Success(Bytes(output).ToArray());
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
    /// A hash of <paramref name="text"/>: what names a compiled shader in the cache, and what it was compiled from.
    /// </summary>
    private static ulong Hash(string text)
    {
        return XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// DXC's <c>DxcBuffer</c>: source text and its encoding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxcBuffer
    {
        public byte* Start;
        public nuint Size;
        public uint Encoding;
    }

    /// <summary>
    /// The start of SDL_shadercross's <c>SDL_ShaderCross_GraphicsShaderMetadata</c>: its resource counts.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GraphicsResources
    {
        public uint Samplers;
        public uint StorageTextures;
        public uint StorageBuffers;
        public uint UniformBuffers;
    }

    /// <summary>
    /// SDL_shadercross's <c>SDL_ShaderCross_ComputePipelineMetadata</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComputeResources
    {
        public uint Samplers;
        public uint StorageTextures;
        public uint StorageBuffers;
        public uint ReadWriteStorageTextures;
        public uint ReadWriteStorageBuffers;
        public uint UniformBuffers;
        public uint ThreadCountX;
        public uint ThreadCountY;
        public uint ThreadCountZ;
    }

    /// <summary>
    /// SDL_shadercross's <c>SDL_ShaderCross_SPIRV_Info</c>. <see cref="Stage"/> is its stage enum, in
    /// <see cref="GpuStage"/>'s order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SpirvInfo
    {
        public byte* Bytecode;
        public nuint BytecodeSize;
        public byte* EntryPoint;
        public int Stage;
        public uint Properties;
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("dxcompiler")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int DxcCreateInstance(Guid* classId, Guid* interfaceId, nint* instance);

        [LibraryImport("SDL3_shadercross", EntryPoint = "SDL_ShaderCross_Init")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool Init();

        [LibraryImport("SDL3_shadercross", EntryPoint = "SDL_ShaderCross_Quit")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial void Quit();

        [LibraryImport("SDL3_shadercross", EntryPoint = "SDL_ShaderCross_ReflectGraphicsSPIRV")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial GraphicsResources* ReflectGraphicsSpirv(byte* bytecode, nuint size, uint properties);

        [LibraryImport("SDL3_shadercross", EntryPoint = "SDL_ShaderCross_ReflectComputeSPIRV")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial ComputeResources* ReflectComputeSpirv(byte* bytecode, nuint size, uint properties);

        [LibraryImport("SDL3_shadercross", EntryPoint = "SDL_ShaderCross_TranspileMSLFromSPIRV")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial byte* TranspileMslFromSpirv(SpirvInfo* info);
    }
}
