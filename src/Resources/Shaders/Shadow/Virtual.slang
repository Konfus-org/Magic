// The sun's virtual shadow map: a clipmap of levels centred on the camera, each a window of pages over the light's
// plane, level n's texels twice level n - 1's. A page is drawn once into the physical pool and kept while the
// receivers keep asking for it and nothing under it moves. C#'s ShadowConstants is the twin of the struct here.
// After Unreal's virtual shadow maps (Karis et al., "A Deep Dive into Nanite Virtualized Geometry" and "Virtual
// Shadow Maps", 2021).
module "Shadow/Virtual";

import Core.Heap;
import Core.Frame;
import Core.Math;

public static const uint ShadowMaxLevels = 24u;

// The table entry's x holds the physical page plus one in its low 16 bits, or 0 for none, and this bit while the
// page waits to be drawn again; its y holds the page's place on the light's plane, PackPage of it.
public static const uint ShadowEntryInvalid = 0x80000000u;

// What a request cell holds while no receiver asked for its page this frame.
public static const uint ShadowNoRequest = 0xFFFFFFFFu;

// What an empty texel of the pool holds: as far from the light as a depth key goes.
public static const uint ShadowClearKey = 0xFFFFFFFFu;

public struct ShadowConstants
{
    // The light's frame: rows of the basis, z the way the light travels.
    public float4 lightX;
    public float4 lightY;
    public float4 lightZ;
    // The camera in the light's frame.
    public float4 cameraLight;

    public uint levels;
    // Pages across a level's window, and texels across a page: both powers of two.
    public uint tableWidth;
    public uint pageTexels;
    // Pages across the physical pool.
    public uint poolPagesPerRow;

    // Metres a level 0 texel covers, and metres a pixel's footprint grows per metre of view depth.
    public float baseTexel;
    public float footprintPerMetre;
    public uint maxDraws;
    public uint maxInvalidPages;

    // Heap ids.
    public uint pageTable;
    public uint pageRequests;
    public uint freeList;
    public uint invalidPages;
    public uint draws;
    public uint drawArguments;
    public uint counts;
    public uint pool;
    public uint dirtyInstances;
    public uint dirtyCount;
    public uint clearArguments;
    public uint padding;

    // Each level's window: the page, counted on the light's plane, at its corner, in xy.
    public int4 windows[ShadowMaxLevels];
}

// The counts buffer's words, as byte offsets.
public static const uint ShadowCountInvalidPages = 0u;
public static const uint ShadowCountDraws = 4u;

// The dispatch arguments buffer that clears and finishes the invalid pages: 16, 16 and the page count.
public static const uint ShadowClearGroupsZ = 8u;

// One draw of the shadow pass: a cluster of an instance into a page.
public struct ShadowDraw
{
    public uint instance;
    public uint cluster;
    // The level in the high half and the cell in the level's table in the low.
    public uint page;
    public uint physical;
}

// An instance that changed this frame: the spheres it had and has, each of no radius when it had none.
public struct DirtyInstance
{
    public float3 oldCenter;
    public float oldRadius;
    public float3 newCenter;
    public float newRadius;
}

public ShadowConstants LoadShadowConstants(uint buffer)
{
    BufferRef constants = { buffer };
    return constants.Load<ShadowConstants>(0u);
}

public float3 ToLight(ShadowConstants shadow, float3 world)
{
    return float3(dot(world, shadow.lightX.xyz), dot(world, shadow.lightY.xyz), dot(world, shadow.lightZ.xyz));
}

public float GetTexelWorld(ShadowConstants shadow, uint level) { return shadow.baseTexel * exp2(float(level)); }

public float GetPageWorld(ShadowConstants shadow, uint level)
{
    return GetTexelWorld(shadow, level) * float(shadow.pageTexels);
}

// Metres a pixel covers at a receiver viewDepth metres from the camera.
public float GetFootprint(ShadowConstants shadow, float viewDepth) { return viewDepth * shadow.footprintPerMetre; }

// The level whose texel is no larger than a receiver's footprint, so a shadow is as sharp as the screen shows it.
public uint GetLevel(ShadowConstants shadow, float footprint)
{
    float level = log2(max(footprint / shadow.baseTexel, 1.0));
    return min(uint(floor(level)), shadow.levels - 1u);
}

// The page, counted on the light's plane, a point of it is in at a level.
public int2 GetPage(ShadowConstants shadow, uint level, float2 light)
{
    return int2(floor(light / GetPageWorld(shadow, level)));
}

public bool IsInWindow(ShadowConstants shadow, uint level, int2 page)
{
    int2 origin = shadow.windows[level].xy;
    return all(page >= origin) && all(page < origin + int(shadow.tableWidth));
}

// The page's cell within its level's table: the window wraps, so a page keeps its cell as the window slides.
public uint GetLocalCell(ShadowConstants shadow, int2 page)
{
    int2 cell = page & int(shadow.tableWidth - 1u);
    return (uint(cell.y) * shadow.tableWidth) + uint(cell.x);
}

// The page's cell in the whole table.
public uint GetCell(ShadowConstants shadow, uint level, int2 page)
{
    return (level * shadow.tableWidth * shadow.tableWidth) + GetLocalCell(shadow, page);
}

public uint PackPage(int2 page) { return (uint(page.x) & 0xFFFFu) | ((uint(page.y) & 0xFFFFu) << 16u); }

// The page a packed entry names, which is within the level's window.
public int2 UnpackPage(ShadowConstants shadow, uint level, uint packed)
{
    int2 origin = shadow.windows[level].xy;
    int2 low = int2(int(packed & 0xFFFFu), int(packed >> 16u));
    return origin + ((low - origin) & 0xFFFF);
}

public uint GetPhysical(uint2 entry) { return (entry.x & 0xFFFFu) - 1u; }

public bool IsAllocated(uint2 entry) { return (entry.x & 0xFFFFu) != 0u; }

public bool IsInvalid(uint2 entry) { return (entry.x & ShadowEntryInvalid) != 0u; }

// The pool texel at a page's top left.
public uint2 GetPoolOrigin(ShadowConstants shadow, uint physical)
{
    return uint2(physical % shadow.poolPagesPerRow, physical / shadow.poolPagesPerRow) * shadow.pageTexels;
}

// A light-space depth as a word that orders as the depth does, so the pool keeps the least by atomics.
public uint ToDepthKey(float z)
{
    uint bits = asuint(z);
    return (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
}

public float FromDepthKey(uint key)
{
    return asfloat((key & 0x80000000u) != 0u ? key & 0x7FFFFFFFu : ~key);
}

// How lit by the sun a receiver is, 0 to 1: its depth against the page's at the level its footprint asks for, or the
// nearest coarser level that has one, filtered over 3 by 3 texels. So a surface does not shadow itself, the receiver
// is first moved along its normal by more the more it slants from the sun, and the page's depth is given a bias
// that grows with the slant too.
public float SampleSunShadow(ShadowConstants shadow, float3 world, float3 normal, float viewDepth)
{
    uint level = GetLevel(shadow, GetFootprint(shadow, viewDepth));
    float cosine = saturate(dot(normal, -shadow.lightZ.xyz));
    float sine = sqrt(saturate(1.0 - cosine * cosine));
    float slant = min(sine / max(cosine, 0.1), 8.0);
    float levelTexel = GetTexelWorld(shadow, level);
    float3 light = ToLight(shadow, world + normal * (levelTexel * (1.0 + 2.0 * sine)));
    ArrayRef<uint2> table = { shadow.pageTable };
    UintTextureRef pool = { shadow.pool };
    for (uint l = level; l < shadow.levels; l++)
    {
        int2 page = GetPage(shadow, l, light.xy);
        if (!IsInWindow(shadow, l, page))
            continue;

        uint2 entry = table[GetCell(shadow, l, page)];
        if (!IsAllocated(entry) || entry.y != PackPage(page))
            continue;

        float pageWorld = GetPageWorld(shadow, l);
        float2 uv = (light.xy - float2(page) * pageWorld) / pageWorld;
        float2 texel = float2(uv.x, 1.0 - uv.y) * float(shadow.pageTexels);
        uint2 origin = GetPoolOrigin(shadow, GetPhysical(entry));
        float bias = GetTexelWorld(shadow, l) * (2.0 + 2.0 * slant);
        float lit = 0.0;
        int last = int(shadow.pageTexels) - 1;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                int2 at = clamp(int2(texel) + int2(x, y), int2(0), int2(last));
                float occluder = FromDepthKey(pool.Load(origin + uint2(at), 0u));
                lit += light.z <= occluder + bias ? 1.0 : 0.0;
            }
        }

        return lit / 9.0;
    }

    return 1.0;
}
