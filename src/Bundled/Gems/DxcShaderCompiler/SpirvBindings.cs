using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// What a SPIR-V module binds, read from its words: each resource variable's name (OpName), what it is (the type its
/// pointer points to: an image sampled or not, or of integers, which no GPU samples, a sampler, a block in the uniform
/// or the storage class) and where it is (its DescriptorSet and Binding decorations), in the binding model of
/// Shaders/Include/Bindings.hlsli. A set is a stage's read, write or uniform space, and a binding is a register of it.
/// A sampler is the sampled texture's at its binding, and is not listed. A compute module's group size comes with them.
/// </summary>
internal static class SpirvBindings
{
    private const uint MagicNumber = 0x07230203;
    private const int HeaderWords = 5;

    private const uint OpExecutionMode = 16;
    private const uint OpName = 5;
    private const uint OpTypeInt = 21;
    private const uint OpTypeImage = 25;
    private const uint OpTypeSampler = 26;
    private const uint OpTypeStruct = 30;
    private const uint OpTypeArray = 28;
    private const uint OpTypeRuntimeArray = 29;
    private const uint OpTypePointer = 32;
    private const uint OpVariable = 59;
    private const uint OpDecorate = 71;

    private const uint ModeLocalSize = 17;
    private const uint DecorationBlock = 2;
    private const uint DecorationBufferBlock = 3;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;

    private const uint ClassUniformConstant = 0;
    private const uint ClassUniform = 2;
    private const uint ClassStorageBuffer = 12;

    /// <summary>
    /// The shader <paramref name="spirv"/> is, compiled for <paramref name="stage"/>, with what it binds; why not, when
    /// it binds something the binding model has no place for.
    /// </summary>
    internal static Result<GpuShader> Read(ReadOnlyMemory<byte> spirv, GpuStage stage)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv.Span);
        if (words.Length < HeaderWords || words[0] != MagicNumber)
        {
            return Result.Failure<GpuShader>("it is not SPIR-V.");
        }

        Dictionary<uint, string> names = [];
        Dictionary<uint, uint> bindings = [];
        Dictionary<uint, uint> sets = [];
        HashSet<uint> blocks = [];
        HashSet<uint> bufferBlocks = [];
        Dictionary<uint, Resource> types = [];
        HashSet<uint> integers = [];
        Dictionary<uint, (uint Class, uint Type)> pointers = [];
        HashSet<uint> arrays = [];
        List<(uint Id, uint Pointer)> variables = [];
        (uint X, uint Y, uint Z) threads = (1, 1, 1);
        int at = HeaderWords;
        while (at < words.Length)
        {
            uint count = words[at] >> 16;
            uint opcode = words[at] & 0xFFFF;
            if (count == 0 || at + count > words.Length) return Result.Failure<GpuShader>("its words are cut short.");

            ReadOnlySpan<uint> operands = words.Slice(at + 1, (int)count - 1);
            switch (opcode)
            {
                case OpName when operands.Length >= 2:
                    names[operands[0]] = ReadLiteral(operands[1..]);
                    break;
                case OpDecorate when operands.Length >= 2:
                    Decorate(operands, bindings, sets, blocks, bufferBlocks);
                    break;
                case OpTypeInt when operands.Length >= 1:
                    integers.Add(operands[0]);
                    break;
                case OpTypeImage when operands.Length >= 7:
                    // An image of integers cannot be sampled, so the binding model reads it as a storage texture.
                    types[operands[0]] = operands[6] == 2 || integers.Contains(operands[1])
                        ? Resource.StorageImage
                        : Resource.Texture;
                    break;
                case OpTypeSampler when operands.Length >= 1:
                    types[operands[0]] = Resource.Sampler;
                    break;
                case OpTypeStruct when operands.Length >= 1:
                    types[operands[0]] = Resource.Block;
                    break;
                case OpTypeArray or OpTypeRuntimeArray when operands.Length >= 2:
                    arrays.Add(operands[0]);
                    break;
                case OpTypePointer when operands.Length >= 3:
                    pointers[operands[0]] = (operands[1], operands[2]);
                    break;
                case OpVariable when operands.Length >= 3:
                    variables.Add((operands[1], operands[0]));
                    break;
                case OpExecutionMode when operands.Length >= 5 && operands[1] == ModeLocalSize:
                    threads = (operands[2], operands[3], operands[4]);
                    break;
                default:
                    break;
            }

            at += (int)count;
        }

        List<GpuShaderBinding> found = [];
        HashSet<uint> samplers = [];
        foreach ((uint id, uint pointer) in variables)
        {
            if (!pointers.TryGetValue(pointer, out (uint Class, uint Type) pointee)
                || pointee.Class is not (ClassUniformConstant or ClassUniform or ClassStorageBuffer))
            {
                continue;
            }

            string name = names.GetValueOrDefault(id, string.Empty);
            if (name.Length == 0) return Result.Failure<GpuShader>("a resource has no name.");

            if (arrays.Contains(pointee.Type))
            {
                return Result.Failure<GpuShader>(
                    $"{name} is an array of resources, which the binding model does not bind.");
            }

            if (!types.TryGetValue(pointee.Type, out Resource resource))
            {
                return Result.Failure<GpuShader>($"{name} is of a type the binding model does not bind.");
            }

            uint binding = bindings.GetValueOrDefault(id, 0u);
            uint set = sets.GetValueOrDefault(id, 0u);
            if (resource == Resource.Sampler)
            {
                samplers.Add(binding);
                continue;
            }

            // A struct is a buffer in the storage class, or a uniform block in the uniform class; nothing else is
            // bound.
            bool isBuffer = resource == Resource.Block
                && (pointee.Class == ClassStorageBuffer || bufferBlocks.Contains(pointee.Type));
            bool isUniform = resource == Resource.Block && pointee.Class == ClassUniform
                && blocks.Contains(pointee.Type);
            if (resource == Resource.Block && !isBuffer && !isUniform)
            {
                return Result.Failure<GpuShader>($"{name} is a struct that is neither a buffer nor a constant block.");
            }

            Result<GpuBindingKind> kind = TryGetKind(stage, set, isBuffer ? Resource.Buffer : resource, name);
            if (!kind.Ok) return Result.Failure<GpuShader>(kind.Error);

            found.Add(new GpuShaderBinding(name, kind.Value, binding));
        }

        foreach (uint binding in samplers)
        {
            if (!found.Exists(texture => texture.Kind == GpuBindingKind.Sampled && texture.Register == binding))
            {
                return Result.Failure<GpuShader>(
                    $"a sampler at {binding} has no sampled texture at {binding}, whose it would be.");
            }
        }

        return Result.Success(new GpuShader
        {
            Code = spirv,
            EntryPoint = "main",
            Stage = stage,
            Bindings = found,
            ThreadCountX = threads.X,
            ThreadCountY = threads.Y,
            ThreadCountZ = threads.Z,
        });
    }

    private static void Decorate(
        ReadOnlySpan<uint> operands,
        Dictionary<uint, uint> bindings,
        Dictionary<uint, uint> sets,
        HashSet<uint> blocks,
        HashSet<uint> bufferBlocks)
    {
        uint target = operands[0];
        switch (operands[1])
        {
            case DecorationBinding when operands.Length >= 3:
                bindings[target] = operands[2];
                break;
            case DecorationDescriptorSet when operands.Length >= 3:
                sets[target] = operands[2];
                break;
            case DecorationBlock:
                blocks.Add(target);
                break;
            case DecorationBufferBlock:
                bufferBlocks.Add(target);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// What a resource in a set is to a stage, by the space the set is (Shaders/Include/Bindings.hlsli): a read space
    /// holds sampled textures, storage textures and storage buffers; compute's write space what it writes; the uniform
    /// space its one constant block.
    /// </summary>
    private static Result<GpuBindingKind> TryGetKind(GpuStage stage, uint set, Resource resource, string name)
    {
        (uint read, uint write, uint uniform) = stage switch
        {
            GpuStage.Vertex => (0u, uint.MaxValue, 1u),
            GpuStage.Fragment => (2u, uint.MaxValue, 3u),
            GpuStage.Compute => (0u, 1u, 2u),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "There is no such shader stage."),
        };
        if (set == uniform && resource == Resource.Block) return Result.Success(GpuBindingKind.Uniform);

        if (set == read)
        {
            return resource switch
            {
                Resource.Texture => Result.Success(GpuBindingKind.Sampled),
                Resource.StorageImage => Result.Success(GpuBindingKind.StorageTexture),
                Resource.Buffer => Result.Success(GpuBindingKind.StorageBuffer),
                Resource.Sampler or Resource.Block => Result.Failure<GpuBindingKind>(
                    $"{name} is read as what the binding model does not read."),
                _ => Result.Failure<GpuBindingKind>($"{name} is read as what the binding model does not read."),
            };
        }

        if (set == write)
        {
            return resource switch
            {
                Resource.StorageImage => Result.Success(GpuBindingKind.ReadWriteStorageTexture),
                Resource.Buffer => Result.Success(GpuBindingKind.ReadWriteStorageBuffer),
                Resource.Texture or Resource.Sampler or Resource.Block => Result.Failure<GpuBindingKind>(
                    $"{name} is written as what the binding model does not write."),
                _ => Result.Failure<GpuBindingKind>($"{name} is written as what the binding model does not write."),
            };
        }

        return Result.Failure<GpuBindingKind>($"{name} is in space {set}, which the stage has no use for.");
    }

    /// <summary>
    /// A literal string operand: UTF-8, ending at its first zero byte.
    /// </summary>
    private static string ReadLiteral(ReadOnlySpan<uint> words)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(words);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private enum Resource : byte
    {
        Texture,
        StorageImage,
        Sampler,
        Buffer,
        Block,
    }
}
