// The heap: every buffer, texture and sampler the GPU provider made, reached by the id its handle holds (C#'s
// GpuBuffer, GpuTexture and GpuSampler). A shader declares no resource of its own: it is handed references among its
// constants and reads and writes through them. A reference's id is clamped to the heap's size before use, as a GPU may
// fetch a descriptor before it knows a branch around the fetch is not taken.
//
// A buffer holds bytes laid out as C# packs a struct: 32-bit scalars and their vectors at their natural alignment,
// so a row is the same bytes on both sides. A texture is read sampled through its whole mip chain, or loaded and
// stored one level at a time as a storage image: level n of texture t is storage slot t * LevelsPerTexture + n.
module "Core/Heap";

// The most of each the heap holds: VulkanHeap's constants.
public static const uint HeapBuffers = 16384u;
public static const uint HeapTextures = 4096u;
public static const uint HeapSamplers = 256u;
public static const uint LevelsPerTexture = 16u;

[[vk::binding(0, 0)]] RWByteAddressBuffer heapBuffers[];
[[vk::binding(1, 0)]] Texture2D<float4> heapTextures2D[];
[[vk::binding(2, 0)]] Texture2DArray<float4> heapTextures2DArray[];
[[vk::binding(3, 0)]] TextureCube<float4> heapCubes[];
[[vk::binding(4, 0)]] Texture3D<float4> heapTextures3D[];
[[vk::binding(5, 0)]] Texture2D<uint> heapUintTextures2D[];
[[vk::binding(6, 0)]] [[vk::image_format("unknown")]] RWTexture2D<float4> heapStorage2D[];
[[vk::binding(7, 0)]] [[vk::image_format("unknown")]] RWTexture2DArray<float4> heapStorage2DArray[];
[[vk::binding(8, 0)]] [[vk::image_format("unknown")]] RWTexture3D<float4> heapStorage3D[];
// Declared R32 so the atomics on it are allowed: every uint storage texture is that format.
[[vk::binding(9, 0)]] [[vk::image_format("r32ui")]] RWTexture2D<uint> heapUintStorage2D[];
[[vk::binding(10, 0)]] SamplerState heapSamplers[];

uint ClampBufferId(uint id) { return min(id, HeapBuffers - 1u); }
uint ClampTextureId(uint id) { return min(id, HeapTextures - 1u); }
uint ClampSamplerId(uint id) { return min(id, HeapSamplers - 1u); }
uint ClampStorageSlot(uint id, uint level) { return (ClampTextureId(id) * LevelsPerTexture) + min(level, LevelsPerTexture - 1u); }

// A buffer of bytes, read and written at byte offsets: what holds words of more than one kind, as counts and the
// arguments of indirect passes do.
public struct BufferRef
{
    public uint id;

    public T Load<T>(uint offset)
    {
        return heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].Load<T>(offset);
    }

    public void Store<T>(uint offset, T value)
    {
        heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].Store<T>(offset, value);
    }

    // Adds value to the word at offset, no other lane's add splitting it, and hands back the word as it was.
    public uint InterlockedAdd(uint offset, uint value)
    {
        uint before;
        heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].InterlockedAdd(offset, value, before);
        return before;
    }

    // Keeps the greater of value and the word at offset.
    public void InterlockedMax(uint offset, uint value)
    {
        heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].InterlockedMax(offset, value);
    }

    // Sets the bits of value in the word at offset.
    public void InterlockedOr(uint offset, uint value)
    {
        heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].InterlockedOr(offset, value);
    }

    // How many bytes the buffer holds.
    public property uint size
    {
        get
        {
            uint bytes;
            heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].GetDimensions(bytes);
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
        get { return heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].Load<T>(index * sizeof(T)); }
        [nonmutating] set { heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].Store<T>(index * sizeof(T), newValue); }
    }

    // How many whole rows the buffer holds.
    public property uint count
    {
        get
        {
            uint bytes;
            heapBuffers[NonUniformResourceIndex(ClampBufferId(id))].GetDimensions(bytes);
            return bytes / sizeof(T);
        }
    }
}

public struct SamplerRef
{
    public uint id;

    public SamplerState Get() { return heapSamplers[NonUniformResourceIndex(ClampSamplerId(id))]; }
}

// A 2D texture read through its mip chain, or one level at a time.
public struct TextureRef
{
    public uint id;

    public float4 Sample(SamplerRef sampler, float2 uv)
    {
        return heapTextures2D[NonUniformResourceIndex(ClampTextureId(id))].Sample(sampler.Get(), uv);
    }

    public float4 SampleLevel(SamplerRef sampler, float2 uv, float level)
    {
        return heapTextures2D[NonUniformResourceIndex(ClampTextureId(id))].SampleLevel(sampler.Get(), uv, level);
    }

    public float4 SampleGrad(SamplerRef sampler, float2 uv, float2 ddx, float2 ddy)
    {
        return heapTextures2D[NonUniformResourceIndex(ClampTextureId(id))].SampleGrad(sampler.Get(), uv, ddx, ddy);
    }

    public float4 Load(uint2 texel, uint level)
    {
        return heapTextures2D[NonUniformResourceIndex(ClampTextureId(id))].Load(int3(texel, level));
    }

    // Texels across and down at a level.
    public uint2 GetSize(uint level)
    {
        uint width;
        uint height;
        uint levels;
        heapTextures2D[NonUniformResourceIndex(ClampTextureId(id))].GetDimensions(level, width, height, levels);
        return uint2(width, height);
    }
}

// A 2D texture of unsigned integers, loaded texel by texel: ids, counts, packed words.
public struct UintTextureRef
{
    public uint id;

    public uint Load(uint2 texel, uint level)
    {
        return heapUintTextures2D[NonUniformResourceIndex(ClampTextureId(id))].Load(int3(texel, level));
    }
}

public struct TextureArrayRef
{
    public uint id;

    public float4 SampleLevel(SamplerRef sampler, float2 uv, uint layer, float level)
    {
        return heapTextures2DArray[NonUniformResourceIndex(ClampTextureId(id))].SampleLevel(sampler.Get(), float3(uv, layer), level);
    }

    public float4 Load(uint2 texel, uint layer, uint level)
    {
        return heapTextures2DArray[NonUniformResourceIndex(ClampTextureId(id))].Load(int4(texel, layer, level));
    }
}

public struct CubeRef
{
    public uint id;

    public float4 SampleLevel(SamplerRef sampler, float3 direction, float level)
    {
        return heapCubes[NonUniformResourceIndex(ClampTextureId(id))].SampleLevel(sampler.Get(), direction, level);
    }
}

public struct VolumeRef
{
    public uint id;

    public float4 SampleLevel(SamplerRef sampler, float3 uvw, float level)
    {
        return heapTextures3D[NonUniformResourceIndex(ClampTextureId(id))].SampleLevel(sampler.Get(), uvw, level);
    }

    public float4 Load(uint3 texel, uint level)
    {
        return heapTextures3D[NonUniformResourceIndex(ClampTextureId(id))].Load(int4(texel, level));
    }
}

// One level of a 2D texture, written and read as a storage image.
public struct StorageRef
{
    public uint id;
    public uint level;

    public float4 Load(uint2 texel)
    {
        return heapStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel];
    }

    public void Store(uint2 texel, float4 value)
    {
        heapStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel] = value;
    }

    public uint2 GetSize()
    {
        uint width;
        uint height;
        heapStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))].GetDimensions(width, height);
        return uint2(width, height);
    }
}

// One level of a 2D texture of unsigned integers, written and read as a storage image, with atomics.
public struct UintStorageRef
{
    public uint id;
    public uint level;

    public uint Load(uint2 texel)
    {
        return heapUintStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel];
    }

    public void Store(uint2 texel, uint value)
    {
        heapUintStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel] = value;
    }

    // Keeps the lesser of value and the texel: what a depth of the sun's pages is written with.
    public void InterlockedMin(uint2 texel, uint value)
    {
        InterlockedMin(heapUintStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel], value);
    }

    public uint2 GetSize()
    {
        uint width;
        uint height;
        heapUintStorage2D[NonUniformResourceIndex(ClampStorageSlot(id, level))].GetDimensions(width, height);
        return uint2(width, height);
    }
}

// One level of a 2D array or cube texture as a storage image, a layer at a time.
public struct StorageArrayRef
{
    public uint id;
    public uint level;

    public float4 Load(uint2 texel, uint layer)
    {
        return heapStorage2DArray[NonUniformResourceIndex(ClampStorageSlot(id, level))][uint3(texel, layer)];
    }

    public void Store(uint2 texel, uint layer, float4 value)
    {
        heapStorage2DArray[NonUniformResourceIndex(ClampStorageSlot(id, level))][uint3(texel, layer)] = value;
    }
}

// One level of a 3D texture as a storage image.
public struct StorageVolumeRef
{
    public uint id;
    public uint level;

    public float4 Load(uint3 texel)
    {
        return heapStorage3D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel];
    }

    public void Store(uint3 texel, float4 value)
    {
        heapStorage3D[NonUniformResourceIndex(ClampStorageSlot(id, level))][texel] = value;
    }
}
