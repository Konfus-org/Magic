using System.Globalization;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;

namespace Magic.ClusterRendering;

/// <summary>
/// One name a stage binds, at its slot among its kind.
/// </summary>
internal readonly record struct BoundName(PassBinding Binding, uint Slot);

/// <summary>
/// What one stage of a pass binds: the names its shader uses, as a probe compile of it found them
/// (<see cref="GpuShader.Bindings"/>), each resolved to what the pass can see and given its slot among its kind, which
/// the renderer binds it at and the shader is compiled again to read it at. A draw's is the union over its templates,
/// which every surface pipeline of it uses no more than (<see cref="Check"/>). <see cref="MakeHeader"/> declares to a
/// shader every name a pass can see, these at their slots and the rest past them, so a shader reads whichever it uses
/// and the compiler drops the rest.
/// </summary>
internal sealed class StageBindings
{
    /// <summary>
    /// The most a stage binds of a kind, as every GPU allows: sampled textures, and storage textures or buffers read or
    /// written.
    /// </summary>
    internal const int MaxSampled = 16;
    internal const int MaxStorage = 8;

    internal List<BoundName> Sampled { get; } = [];

    /// <summary>
    /// The textures read texel by texel with no sampler (<see cref="ResourceInfo.IsStorageRead"/>).
    /// </summary>
    internal List<BoundName> StorageReads { get; } = [];

    internal List<BoundName> Buffers { get; } = [];

    internal List<BoundName> TextureWrites { get; } = [];

    internal List<BoundName> BufferWrites { get; } = [];

    /// <summary>
    /// What the names <paramref name="used"/> by a shader, or the shaders of a draw's templates together, bind: each
    /// what the pass sees it as, in the kind the shader reads it as, which must be the name's own; each kind's slots in
    /// the order the pass sees the names. Why not, when a name is read as what it is not, or more are used than a
    /// stage binds.
    /// </summary>
    internal static Result<StageBindings> TryCreate(
        IReadOnlyList<GpuShaderBinding> used,
        IReadOnlyList<ResourceInfo> visible,
        IReadOnlyList<PassBinding> writes)
    {
        Dictionary<string, ResourceInfo> seen = new(StringComparer.Ordinal);
        foreach (ResourceInfo info in visible)
        {
            seen[info.Name] = info;
        }

        Dictionary<string, PassBinding> written = new(StringComparer.Ordinal);
        foreach (PassBinding write in writes)
        {
            written[write.Info.Name] = write;
        }

        Dictionary<string, GpuBindingKind> kinds = new(StringComparer.Ordinal);
        foreach (GpuShaderBinding binding in used)
        {
            if (binding.Kind == GpuBindingKind.Uniform) continue;

            if (kinds.TryGetValue(binding.Name, out GpuBindingKind kind) && kind != binding.Kind)
            {
                return Result.Failure<StageBindings>($"{binding.Name} is read as two kinds of thing.");
            }

            kinds[binding.Name] = binding.Kind;
            if (GetKindError(binding, seen, written) is { } why) return Result.Failure<StageBindings>(why);
        }

        StageBindings bound = new();
        foreach (ResourceInfo info in visible)
        {
            if (kinds.TryGetValue(info.Name, out GpuBindingKind kind)
                && kind is GpuBindingKind.Sampled or GpuBindingKind.StorageTexture or GpuBindingKind.StorageBuffer)
            {
                List<BoundName> list = bound.GetList(kind);
                list.Add(new BoundName(new PassBinding(info, 0, 0, GpuContents.Keep, false), (uint)list.Count));
            }
        }

        foreach (PassBinding write in writes)
        {
            if (kinds.TryGetValue(write.Info.Name, out GpuBindingKind kind)
                && kind is GpuBindingKind.ReadWriteStorageTexture or GpuBindingKind.ReadWriteStorageBuffer)
            {
                List<BoundName> list = bound.GetList(kind);
                list.Add(new BoundName(write, (uint)list.Count));
            }
        }

        return bound.Sampled.Count > MaxSampled || bound.StorageReads.Count > MaxStorage
            || bound.Buffers.Count > MaxStorage
            || bound.TextureWrites.Count > MaxStorage
            || bound.BufferWrites.Count > MaxStorage
            ? Result.Failure<StageBindings>($"its shader uses {bound.Sampled.Count} sampled textures, "
                + $"{bound.StorageReads.Count} storage textures, {bound.Buffers.Count} buffers and writes "
                + $"{bound.TextureWrites.Count} textures and {bound.BufferWrites.Count} buffers, "
                + $"where a stage binds {MaxSampled} sampled textures and {MaxStorage} of each of the rest at most.")
            : Result.Success(bound);
    }

    /// <summary>
    /// The declarations a shader of <paramref name="stage"/> is given: every name the pass can see, as it reads it, a
    /// sampled texture with its sampler, <c>HAS_Name</c> defined for each it can read; and for compute every name the
    /// pass writes, as it writes it. With <paramref name="bound"/>, each name it holds is at its slot and the rest are
    /// numbered past them, where the compiler drops them unused; without, they are numbered in order, sampled textures
    /// first, then storage textures, then buffers, as the binding model counts them, for the probe that finds which are
    /// used.
    /// </summary>
    internal static string MakeHeader(
        GpuStage stage,
        IReadOnlyList<ResourceInfo> visible,
        IReadOnlyList<PassBinding> writes,
        StageBindings? bound)
    {
        StringBuilder header = new("#include \"Include/Frame.hlsli\"\n#include \"Include/Structs.hlsli\"\n");
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (PassBinding write in writes)
        {
            written.Add(write.Info.Name);
        }

        foreach (ResourceInfo info in visible)
        {
            if (!written.Contains(info.Name))
            {
                header.Append(CultureInfo.InvariantCulture, $"#define HAS_{info.Name} 1\n");
            }
        }

        uint sampled = (uint)(bound?.Sampled.Count ?? 0);
        uint storageReads = (uint)(bound?.StorageReads.Count ?? 0);
        uint next = sampled + storageReads + (uint)(bound?.Buffers.Count ?? 0);
        foreach (ResourceInfo info in visible)
        {
            if (!info.IsBuffer && !info.IsStorageRead && !written.Contains(info.Name))
            {
                uint read = GetSlot(bound?.Sampled, info.Name, 0, ref next);
                string sampler = info.Filter == PassFilter.Comparison ? "SamplerComparisonState" : "SamplerState";
                header.Append(
                    CultureInfo.InvariantCulture,
                    $"{info.ReadType} {info.Name} : READ({read});\n{sampler} {info.Name}Sampler : SAMPLER({read});\n");
            }
        }

        foreach (ResourceInfo info in visible)
        {
            if (!info.IsBuffer && info.IsStorageRead && !written.Contains(info.Name))
            {
                header.Append(CultureInfo.InvariantCulture, $"{info.ReadType} {info.Name} : "
                    + $"READ({GetSlot(bound?.StorageReads, info.Name, sampled, ref next)});\n");
            }
        }

        foreach (ResourceInfo info in visible)
        {
            if (info.IsBuffer && !written.Contains(info.Name))
            {
                header.Append(CultureInfo.InvariantCulture, $"{info.ReadType} {info.Name} : "
                    + $"READ({GetSlot(bound?.Buffers, info.Name, sampled + storageReads, ref next)});\n");
            }
        }

        if (stage != GpuStage.Compute) return header.ToString();

        uint writtenTextures = (uint)(bound?.TextureWrites.Count ?? 0);
        uint nextWrite = writtenTextures + (uint)(bound?.BufferWrites.Count ?? 0);
        foreach (PassBinding write in writes)
        {
            if (!write.Info.IsBuffer)
            {
                header.Append(CultureInfo.InvariantCulture, $"{write.Info.WriteType} {write.Info.Name} : "
                    + $"WRITE({GetSlot(bound?.TextureWrites, write.Info.Name, 0, ref nextWrite)});\n");
            }
        }

        foreach (PassBinding write in writes)
        {
            if (write.Info.IsBuffer)
            {
                header.Append(CultureInfo.InvariantCulture, $"{write.Info.WriteType} {write.Info.Name} : "
                    + $"WRITE({GetSlot(bound?.BufferWrites, write.Info.Name, writtenTextures, ref nextWrite)});\n");
            }
        }

        return header.ToString();
    }

    /// <summary>
    /// <paramref name="shader"/> as its pipeline binds it: this stage's bindings, which it must use no more than, each
    /// at the register it is bound at, and its own constant block. Why not, when it uses a name past them or at another
    /// register.
    /// </summary>
    internal Result<GpuShader> Check(GpuShader shader, string name)
    {
        List<GpuShaderBinding> bindings = ToShaderBindings();
        foreach (GpuShaderBinding binding in shader.Bindings)
        {
            if (binding.Kind == GpuBindingKind.Uniform)
            {
                bindings.Add(binding);
                continue;
            }

            List<BoundName> list = GetList(binding.Kind);
            if (!list.Exists(bound => string.Equals(bound.Binding.Info.Name, binding.Name, StringComparison.Ordinal)
                && RegisterOf(binding.Kind, bound.Slot) == binding.Register))
            {
                return Result.Failure<GpuShader>(
                    $"{name} uses {binding.Name} at {binding.Register}, which its pass does not bind there.");
            }
        }

        return Result.Success(shader with { Bindings = bindings });
    }

    /// <summary>
    /// Why a shader reads a name as what it is not: a sampled texture that is a buffer, a write of what the pass does
    /// not write, or a name the pass does not see.
    /// </summary>
    private static string? GetKindError(
        GpuShaderBinding binding,
        Dictionary<string, ResourceInfo> seen,
        Dictionary<string, PassBinding> written)
    {
        bool isWrite = binding.Kind is GpuBindingKind.ReadWriteStorageTexture or GpuBindingKind.ReadWriteStorageBuffer;
        if (isWrite)
        {
            if (!written.TryGetValue(binding.Name, out PassBinding write))
            {
                return $"its shader writes {binding.Name}, which its file does not write.";
            }

            if (write.Info.IsBuffer == (binding.Kind == GpuBindingKind.ReadWriteStorageBuffer)) return null;

            string kind = write.Info.IsBuffer ? "texture" : "buffer";
            return $"its shader writes {binding.Name} as a {kind}, which it is not.";
        }

        if (!seen.TryGetValue(binding.Name, out ResourceInfo? info))
        {
            return $"its shader reads {binding.Name}, which it cannot see.";
        }

        if (binding.Kind == GpuBindingKind.Sampled && (info.IsBuffer || info.IsStorageRead))
        {
            return $"its shader samples {binding.Name}, "
                + $"which is {(info.IsBuffer ? "a buffer" : "read texel by texel, having no sampler")}.";
        }

        if (binding.Kind == GpuBindingKind.StorageTexture && (info.IsBuffer || !info.IsStorageRead))
        {
            return $"its shader reads {binding.Name} as a storage texture, which it is not.";
        }

        return binding.Kind == GpuBindingKind.StorageBuffer && !info.IsBuffer
            ? $"its shader reads {binding.Name} as a buffer, which it is not."
            : null;
    }

    /// <summary>
    /// The register a name is declared at: its own slot in <paramref name="list"/> past <paramref name="before"/>, the
    /// registers of the kinds before it, else the next number past every bound name.
    /// </summary>
    private static uint GetSlot(List<BoundName>? list, string name, uint before, ref uint next)
    {
        if (list is not null)
        {
            foreach (BoundName bound in list)
            {
                if (string.Equals(bound.Binding.Info.Name, name, StringComparison.Ordinal)) return before + bound.Slot;
            }
        }

        return next++;
    }

    private List<BoundName> GetList(GpuBindingKind kind) => kind switch
    {
        GpuBindingKind.Sampled => Sampled,
        GpuBindingKind.StorageTexture => StorageReads,
        GpuBindingKind.StorageBuffer => Buffers,
        GpuBindingKind.ReadWriteStorageTexture => TextureWrites,
        GpuBindingKind.ReadWriteStorageBuffer => BufferWrites,
        GpuBindingKind.Uniform => [],
        _ => [],
    };

    /// <summary>
    /// The register a slot of a kind is at: a read space's registers run sampled textures, then storage textures, then
    /// buffers; compute's write space its written textures, then buffers.
    /// </summary>
    private uint RegisterOf(GpuBindingKind kind, uint slot) => slot + kind switch
    {
        GpuBindingKind.StorageTexture => (uint)Sampled.Count,
        GpuBindingKind.StorageBuffer => (uint)(Sampled.Count + StorageReads.Count),
        GpuBindingKind.ReadWriteStorageBuffer => (uint)TextureWrites.Count,
        GpuBindingKind.Sampled or GpuBindingKind.Uniform or GpuBindingKind.ReadWriteStorageTexture => 0u,
        _ => 0u,
    };

    private List<GpuShaderBinding> ToShaderBindings()
    {
        List<GpuShaderBinding> bindings = [];
        Add(Sampled, GpuBindingKind.Sampled);
        Add(StorageReads, GpuBindingKind.StorageTexture);
        Add(Buffers, GpuBindingKind.StorageBuffer);
        Add(TextureWrites, GpuBindingKind.ReadWriteStorageTexture);
        Add(BufferWrites, GpuBindingKind.ReadWriteStorageBuffer);
        return bindings;

        void Add(List<BoundName> list, GpuBindingKind kind)
        {
            foreach (BoundName bound in list)
            {
                bindings.Add(new GpuShaderBinding(bound.Binding.Info.Name, kind, RegisterOf(kind, bound.Slot)));
            }
        }
    }
}
