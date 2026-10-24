// The world as a frame draws it and the view a pass is drawn for: the renderer's WorldConstants and ViewConstants
// (GpuStructs.cs), member for member, which every pass loads first (BeginPass) into World and View, which everything
// after reads; the rows of the world's tables; and what an instance's row does to a point of its model.
//
// Camera-relative: a position is relative to the camera before viewProj takes it. Reverse-Z with no far plane: a depth
// is near over the view depth, 0 where nothing was drawn. A matrix is row-major, as System.Numerics keeps it, and
// takes a row vector: mul(v, M). An instance's transform is three float4 rows of transpose(world), applied with dot().
//
// A new frame global is a field of WorldConstants here and in its C# twin, which Views.GetWorld fills.

module "Renderer/World";

__exported import Core.Pass;
__exported import Core.Sky;
__exported import Lighting.Lights;

// An instance's flags, the renderer's GpuInstanceFlags.
static const uint InstanceMirrored = 1u;
static const uint InstanceFadeIn = 2u;
static const uint InstanceShadowless = 4u;

// An instance, by its entity's slot: the rows of transpose(world) (xyz its turn and scale, w its position), and the
// last frame's the same way; its largest scale, its model's row, when its fade started (real seconds) and its eight
// material slots' rows, two to a word.
public struct GpuInstance
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float maxScale;
    uint flags;
    uint model;
    float fadeStartSec;
    uint4 materials;
}

// A model: its sphere about its origin, its clusters, levels and cards, the unit-scale distance its cards are drawn
// from (0 for none), its grid's step, and how far from its sphere's middle any cluster's error sphere or its parent's
// reaches. Padded to a whole number of 16 bytes.
public struct GpuModel
{
    float4 bounds;
    uint firstCluster;
    uint clusterCount;
    uint firstLevel;
    uint levelCount;
    uint firstCard;
    uint cardCount;
    float cardDistance;
    float positionStep;
    float clusterReach;
    uint padding0;
    uint padding1;
    uint padding2;
}

// One level of a model's clusters: where they are, the least error of any of them, and the most of any of their
// parents'.
public struct GpuLevel
{
    uint firstCluster;
    uint count;
    float minError;
    float maxParentError;
}

// One cluster: its sphere, its group's sphere and error and its parent's, where its vertices and triangles start in the
// geometry's words, its counts (triangles in the low 8 bits, vertices in the next 9, its material slot in the 3 after,
// its level in the 5 after) and its model's grid step. Padded to a whole number of 16 bytes.
public struct GpuCluster
{
    float4 bounds;
    float4 errorBounds;
    float4 parentErrorBounds;
    float error;
    float parentError;
    uint vertices;
    uint triangles;
    uint counts;
    float step;
    uint padding0;
    uint padding1;
}

// One card of a model: its centre, half width (less than 0 when it shows its mesh mirrored), half height and turn about
// +Y, its material slot, and its two atlases, none while they load. Padded to a whole number of 16 bytes.
public struct GpuCard
{
    float3 centre;
    float halfWidth;
    float halfHeight;
    float turn;
    uint slot;
    TextureRef surface;
    TextureRef normals;
    uint padding0;
    uint padding1;
    uint padding2;
}

public struct ViewConstants
{
    float4x4 viewProj;
    float4x4 rotation;       // world to view, a rotation alone
    float4x4 prevViewProj;   // the last frame's viewProj

    float3 cameraPosition;
    float clusterError;      // pixels a cluster's error may cover before a finer one is drawn

    float2 viewSize;         // pixels as the view is drawn, and one over them
    float2 viewTexel;

    float2 projScale;        // the projection's x and y scales
    float near;
    float far;

    float2 outputSize;       // pixels as the view is shown
    float2 jitter;           // this frame's sub-pixel offset of viewProj, in clip space

    float3 cameraMotion;     // the camera's position less the last frame's
    float pixelScale;        // pixels a metre covers one metre in front of the camera, along the view's height

    float3 priorityPosition; // the player's camera relative to this one, which detail is kept nearest to
    float textureMipScale;   // what uv gradients are scaled by

    uint targetEncoding;     // Core/Color's TargetEncoding
    ArrayRef<float4> exposure; // the view's exposure, its EV100, and whether it was metered (Post/Exposure)
}

public struct WorldConstants
{
    FrameConstants clocks;   // first, as the pass header's frame buffer starts with them (Core/Pass)
    WorldLights lights;

    // The world's tables.
    ArrayRef<GpuInstance> instances;
    ArrayRef<LocalLight> localLights; // its point and spot lights, by entity slot; a slot with none reaches nowhere
    ArrayRef<GpuModel> models;
    ArrayRef<GpuLevel> levels;
    ArrayRef<GpuCluster> clusters;
    ArrayRef<GpuCard> cards;
    BufferRef materials;          // each row a surface's or a sky's struct, MaterialBytes apart
    ArrayRef<uint> materialBins;  // each material row's bins word (Renderer/Culling)
    BufferRef geometry;           // the clusters' vertices and triangles, packed words (Renderer/Pieces)

    uint skyMaterial;             // the sky's material's row
    SamplerRef materialSampler;   // what materials' textures are read through
    ArrayRef<uint> failures;      // the frame's raised failure codes (Core/Failure)
    ArrayRef<float4> skySh;       // the sky's light from everywhere, nine harmonics
    WeatherConstants weather;
    CelestialConstants celestial;
}

// The bytes a material's row holds at most: a surface's or a sky's struct is laid out within them.
public static const uint MaterialBytes = 128u;

// The world of the frame the pass is drawn in, and the view it is drawn for, which BeginPass loads.
public static WorldConstants World = {};
public static ViewConstants View = {};

// Loads the world and the view a pass is drawn for, from its header: the first thing every pass does.
public void BeginPass(PassHeader pass)
{
    World = pass.frame.Load<WorldConstants>(0u);
    View = pass.view.Load<ViewConstants>(0u);
}

// Loads the world alone, for a pass of the whole frame, drawn for no view, which reads no View: the first thing such a
// pass does.
public void BeginFramePass(PassHeader pass)
{
    World = pass.frame.Load<WorldConstants>(0u);
}

public extension WorldConstants
{
    // A material row's surface or sky, laid out as its struct.
    public T LoadMaterial<T>(uint row)
    {
        return materials.Load<T>(row * MaterialBytes);
    }
}

public extension ViewConstants
{
    // A camera-relative position through the view-projection, and one relative to the last frame's camera through the
    // last frame's.
    public float4 ToClip(float3 position)
    {
        return mul(float4(position, 1.0), viewProj);
    }

    public float4 ToLastClip(float3 position)
    {
        return mul(float4(position, 1.0), prevViewProj);
    }

    // The distance along the view's +Z a depth stands for; only for a depth above 0.
    public float GetViewDepth(float depth)
    {
        return near / max(depth, 1e-12);
    }

    // A view-space position from a point of the view (pixels from its top left, a pixel's centre at +0.5) and its
    // depth, the frame's jitter taken back out.
    public float3 ToViewPosition(float2 pixel, float viewDepth)
    {
        float2 ndc = float2(pixel.x * viewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * viewTexel.y * 2.0) - jitter;
        return float3(ndc / projScale * viewDepth, viewDepth);
    }

    // Where a view-space position in front of the camera falls on the view, in pixels from its top left.
    public float2 ToViewPixel(float3 seen)
    {
        float2 ndc = seen.xy * projScale / seen.z + jitter;
        return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * viewSize;
    }

    // A direction from view space to world space, the rotation's transpose undoing it, and back.
    public float3 ToWorld(float3 direction)
    {
        return float3(dot(direction, rotation[0].xyz), dot(direction, rotation[1].xyz), dot(direction, rotation[2].xyz));
    }

    public float3 ToView(float3 direction)
    {
        return mul(float4(direction, 0.0), rotation).xyz;
    }
}

public extension GpuInstance
{
    // Its scale turns it inside out, so its triangles wind the other way.
    public property bool isMirrored
    {
        get { return (flags & InstanceMirrored) != 0u; }
    }

    // It dissolves into view from fadeStartSec.
    public property bool isFadingIn
    {
        get { return (flags & InstanceFadeIn) != 0u; }
    }

    // It casts no shadow: light passes through it (Core's Renderer.IsShadowless).
    public property bool isShadowless
    {
        get { return (flags & InstanceShadowless) != 0u; }
    }

    // Where a point of its model is in the world.
    public float3 ToWorld(float3 position)
    {
        float3 turned = float3(dot(row0.xyz, position), dot(row1.xyz, position), dot(row2.xyz, position));
        return turned + float3(row0.w, row1.w, row2.w);
    }

    // Where a point of its model is relative to the camera, and was relative to the last frame's.
    public float3 ToCameraRelative(float3 position)
    {
        float3 turned = float3(dot(row0.xyz, position), dot(row1.xyz, position), dot(row2.xyz, position));
        float3 moved = float3(row0.w, row1.w, row2.w) - View.cameraPosition;
        return turned + moved;
    }

    public float3 ToLastCameraRelative(float3 position)
    {
        float3 turned = float3(dot(prevRow0.xyz, position), dot(prevRow1.xyz, position), dot(prevRow2.xyz, position));
        float3 moved = float3(prevRow0.w, prevRow1.w, prevRow2.w) - View.cameraPosition;
        return turned + moved + View.cameraMotion;
    }

    // A direction of its model in the world, turned and scaled as the instance is.
    public float3 ToWorldDirection(float3 direction)
    {
        return float3(dot(row0.xyz, direction), dot(row1.xyz, direction), dot(row2.xyz, direction));
    }

    // A normal of its model in the world: by its cofactors, so a scale that is not even keeps it at right angles to the
    // surface, turned back where the transform mirrors.
    public float3 ToWorldNormal(float3 normal)
    {
        float3 cofactor0 = cross(row1.xyz, row2.xyz);
        float3 cofactor1 = cross(row2.xyz, row0.xyz);
        float3 cofactor2 = cross(row0.xyz, row1.xyz);
        float3 turned = float3(dot(cofactor0, normal), dot(cofactor1, normal), dot(cofactor2, normal));
        return NormalizeOrZero(dot(row0.xyz, cofactor0) < 0.0 ? -turned : turned);
    }

    // A material slot's row.
    public uint GetMaterial(uint slot)
    {
        uint word = materials[slot >> 1];
        return (slot & 1u) != 0u ? word >> 16 : word & 0xFFFFu;
    }
}
