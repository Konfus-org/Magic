// The heap: every buffer, texture and sampler the GPU made, which a shader reaches through the id its handle holds
// (C#'s GpuBuffer, GpuTexture and GpuSampler), held in a reference that says what it is: BufferRef for bytes,
// ArrayRef<T> for rows of T, TextureRef and its kin for textures, SamplerRef for a sampler. A shader binds nothing: it
// is handed references among its constants and reads and writes through them, and its pass lists what it reads and
// writes (GpuComputePass.Reads and Writes), which the GPU waits for.
//
// A reference indexes the heap non-uniformly, so it may differ from lane to lane. A texture reference keeps its index
// inside the heap, as a texture may be none (TextureNone) and a GPU may fetch the descriptor before it knows that a
// branch skipping the read is not taken. A buffer holds what it holds laid out as C# packs a struct (scalar): a row is
// the same bytes on both sides. A texture a pass writes is laid out to be written in that pass and read in a pass
// after, so no pass reads and writes the same texture.

module "Core/Heap";

// How many levels each texture has room for in the arrays of levels, a texture's level at its id times this plus the
// level: VulkanHeap.LevelsPerTexture.
static const uint LevelsPerTexture = 16u;

// The most textures the heap holds: VulkanHeap.WantedTextures.
static const uint HeapTextures = 8192u;

[[vk::binding(0, 0)]] RWByteAddressBuffer heapBuffers[];
[[vk::binding(1, 0)]] Texture2D heapTextures[];
[[vk::binding(3, 0)]] TextureCube heapCubes[];
[[vk::binding(4, 0)]] Texture3D heapVolumes[];
[[vk::binding(5, 0)]] Texture2D<uint> heapUintTextures[];
[[vk::binding(6, 0)]] [[vk::image_format("unknown")]] RWTexture2D<float4> heapLevels[];
[[vk::binding(7, 0)]] [[vk::image_format("unknown")]] RWTexture2DArray<float4> heapArrayLevels[];
[[vk::binding(8, 0)]] [[vk::image_format("unknown")]] RWTexture3D<float4> heapVolumeLevels[];
[[vk::binding(10, 0)]] SamplerState heapSamplers[];

// A buffer of bytes, read and written at byte offsets: what holds words of more than one kind, as counts and the
// arguments of indirect passes.
public struct BufferRef
{
    public uint id;

    public T Load<T>(uint offset)
    {
        return heapBuffers[NonUniformResourceIndex(id)].Load<T>(offset);
    }

    public void Store<T>(uint offset, T value)
    {
        heapBuffers[NonUniformResourceIndex(id)].Store<T>(offset, value);
    }

    // Adds value to the word at offset in one step no other lane's add splits, and hands back the word before it.
    public uint InterlockedAdd(uint offset, uint value)
    {
        uint before;
        heapBuffers[NonUniformResourceIndex(id)].InterlockedAdd(offset, value, before);
        return before;
    }

    // How many bytes the buffer holds.
    public property uint size
    {
        get
        {
            uint bytes;
            heapBuffers[NonUniformResourceIndex(id)].GetDimensions(bytes);
            return bytes;
        }
    }
}

// A buffer of rows of T, read and written by index.
public struct ArrayRef<T>
{
    public uint id;

    public __subscript(uint index) -> T
    {
        get { return heapBuffers[NonUniformResourceIndex(id)].Load<T>(index * sizeof(T)); }
        [nonmutating] set { heapBuffers[NonUniformResourceIndex(id)].Store<T>(index * sizeof(T), newValue); }
    }

    // How many whole rows the buffer holds.
    public property uint count
    {
        get
        {
            uint bytes;
            heapBuffers[NonUniformResourceIndex(id)].GetDimensions(bytes);
            return bytes / sizeof(T);
        }
    }
}

public extension ArrayRef<uint>
{
    // Adds value to the row at index in one step no other lane's add splits, and hands back the row before it.
    public uint InterlockedAdd(uint index, uint value)
    {
        uint before;
        heapBuffers[NonUniformResourceIndex(id)].InterlockedAdd(index * 4u, value, before);
        return before;
    }

    // The row at index as the atomics of every group of the dispatch so far left it, which a plain load may not see.
    public uint LoadAtomic(uint index)
    {
        return InterlockedAdd(index, 0u);
    }

    // Stores value at index as an atomic, which another group's LoadAtomic sees.
    public void StoreAtomic(uint index, uint value)
    {
        uint before;
        heapBuffers[NonUniformResourceIndex(id)].InterlockedExchange(index * 4u, value, before);
    }

    // Keeps the least, the most, or the bits of both, of the row at index and value, in one step no other lane splits.
    public void InterlockedMin(uint index, uint value)
    {
        heapBuffers[NonUniformResourceIndex(id)].InterlockedMin(index * 4u, value);
    }

    public void InterlockedMax(uint index, uint value)
    {
        heapBuffers[NonUniformResourceIndex(id)].InterlockedMax(index * 4u, value);
    }

    public void InterlockedOr(uint index, uint value)
    {
        heapBuffers[NonUniformResourceIndex(id)].InterlockedOr(index * 4u, value);
    }
}

// A sampler: how a texture is filtered and addressed.
public struct SamplerRef
{
    public uint id;
}

// A 2D texture of floats, every level: sampled or loaded, and each level written (GetLevel) by the pass it is laid out
// for.
public struct TextureRef
{
    public uint id;

    // Whether it names a texture rather than none: a texture still loading is none.
    public property bool isValid
    {
        get { return id != 0xFFFFFFFFu; }
    }

    // With the derivatives of uv across the pixel: only outside a branch some lanes skip.
    public float4 Sample(SamplerRef sampler, float2 uv)
    {
        return Texture().Sample(heapSamplers[NonUniformResourceIndex(sampler.id)], uv);
    }

    public float4 SampleLevel(SamplerRef sampler, float2 uv, float level)
    {
        return Texture().SampleLevel(heapSamplers[NonUniformResourceIndex(sampler.id)], uv, level);
    }

    // With uv's own derivatives across the pixel, so a branch may hold it.
    public float4 SampleGrad(SamplerRef sampler, float2 uv, float2 uvDdx, float2 uvDdy)
    {
        return Texture().SampleGrad(heapSamplers[NonUniformResourceIndex(sampler.id)], uv, uvDdx, uvDdy);
    }

    public float4 Load(int2 texel, int level)
    {
        return Texture().Load(int3(texel, level));
    }

    // A level's size, in texels.
    public uint2 GetSize(uint level)
    {
        uint width;
        uint height;
        uint levels;
        Texture().GetDimensions(level, width, height, levels);
        return uint2(width, height);
    }

    public LevelRef GetLevel(uint level)
    {
        return { min(id, HeapTextures - 1u) * LevelsPerTexture + level };
    }

    Texture2D Texture()
    {
        return heapTextures[NonUniformResourceIndex(min(id, HeapTextures - 1u))];
    }
}

// No texture: what a material's texture is until one is named and loaded.
public static const TextureRef TextureNone = { 0xFFFFFFFFu };

// One level of a 2D texture of floats, read and written texel by texel.
public struct LevelRef
{
    uint index;

    public __subscript(uint2 texel) -> float4
    {
        get { return heapLevels[NonUniformResourceIndex(index)][texel]; }
        [nonmutating] set { heapLevels[NonUniformResourceIndex(index)][texel] = newValue; }
    }

    // In texels.
    public property uint2 size
    {
        get
        {
            uint width;
            uint height;
            heapLevels[NonUniformResourceIndex(index)].GetDimensions(width, height);
            return uint2(width, height);
        }
    }
}

// A 2D texture of integers (r32_uint), loaded texel by texel: integers are never sampled.
public struct UintTextureRef
{
    public uint id;

    public uint Load(int2 texel, int level)
    {
        return heapUintTextures[NonUniformResourceIndex(min(id, HeapTextures - 1u))].Load(int3(texel, level));
    }
}

// A cube texture, every level, sampled along a direction, and each level's six faces written (GetLevel) as layers.
public struct CubeRef
{
    public uint id;

    public float4 SampleLevel(SamplerRef sampler, float3 direction, float level)
    {
        return Cube().SampleLevel(heapSamplers[NonUniformResourceIndex(sampler.id)], direction, level);
    }

    // A face's size at level 0, in texels, and how many levels it has.
    public property uint3 size
    {
        get
        {
            uint width;
            uint height;
            uint levels;
            Cube().GetDimensions(0u, width, height, levels);
            return uint3(width, height, levels);
        }
    }

    public FacesRef GetLevel(uint level)
    {
        return { min(id, HeapTextures - 1u) * LevelsPerTexture + level };
    }

    TextureCube Cube()
    {
        return heapCubes[NonUniformResourceIndex(min(id, HeapTextures - 1u))];
    }
}

// One level of a cube's six faces, read and written texel by texel, the face (Core/Math's GetCubeDirection) the third
// coordinate.
public struct FacesRef
{
    uint index;

    public __subscript(uint3 texel) -> float4
    {
        get { return heapArrayLevels[NonUniformResourceIndex(index)][texel]; }
        [nonmutating] set { heapArrayLevels[NonUniformResourceIndex(index)][texel] = newValue; }
    }

    // A face's size in texels.
    public property uint2 size
    {
        get
        {
            uint width;
            uint height;
            uint layers;
            heapArrayLevels[NonUniformResourceIndex(index)].GetDimensions(width, height, layers);
            return uint2(width, height);
        }
    }
}

// A 3D texture of floats, every level: sampled at a point, and each level written (GetLevel) by the pass it is laid out
// for.
public struct VolumeRef
{
    public uint id;

    public float4 SampleLevel(SamplerRef sampler, float3 uvw, float level)
    {
        return Volume().SampleLevel(heapSamplers[NonUniformResourceIndex(sampler.id)], uvw, level);
    }

    public float4 Load(int3 texel, int level)
    {
        return Volume().Load(int4(texel, level));
    }

    public VolumeLevelRef GetLevel(uint level)
    {
        return { min(id, HeapTextures - 1u) * LevelsPerTexture + level };
    }

    Texture3D Volume()
    {
        return heapVolumes[NonUniformResourceIndex(min(id, HeapTextures - 1u))];
    }
}

// One level of a 3D texture of floats, read and written texel by texel.
public struct VolumeLevelRef
{
    uint index;

    public __subscript(uint3 texel) -> float4
    {
        get { return heapVolumeLevels[NonUniformResourceIndex(index)][texel]; }
        [nonmutating] set { heapVolumeLevels[NonUniformResourceIndex(index)][texel] = newValue; }
    }
}
