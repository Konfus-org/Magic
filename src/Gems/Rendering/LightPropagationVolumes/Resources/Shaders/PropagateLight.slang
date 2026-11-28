// The sun's light that bounces off the lit surfaces, spread through a grid of cells around the camera (Kaplanyan,
// "Light Propagation Volumes in CryEngine 3", SIGGRAPH 2009; Kaplanyan and Dachsbacher, "Cascaded Light Propagation
// Volumes for Real-Time Indirect Illumination", I3D 2010). Each cell holds the light that leaves it as an intensity in
// each direction, in 4 coefficients of spherical harmonics for each colour. The passes, in order:
// - Inject: each texel of the sun's reflective shadow map that lies in a cell is a small light there, of its diffuse
//   colour times the sun's light on it, facing along its normal.
// - Find the inside: each cell whose centre is in an interior box is inside. A texel puts its light only into a cell
//   that is inside as the texel is.
// - Propagate, a number of times: each cell takes the light that its 6 neighbours send through its faces, from a
//   neighbour that is inside as it is, and adds it to the sum.
// - Light pixels: each pixel at half size reads the sum at its surface, from the cells that are inside as it is, and
//   turns it into the light that reaches it.
// The pass header's x is the frame's buffer, y the buffer of VolumeConstants, and z the propagation's step.
module "PropagateLight";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Scene.ShadowViews;
import Light.Ambient;
import Sky.ISky;

// What the passes read beyond the frame, C#'s VolumeConstants, 144 bytes.
struct VolumeConstants
{
    // From the clip space of the reflective shadow map to the camera-relative world.
    float4x4 clipToRelative;
    // The camera-relative corner of the grid at its lowest x, y and z, and a cell's side in metres.
    float3 origin;
    float cell;
    // The cells along each side of the grid, the two volumes of light that the steps go between, and the sum.
    uint size;
    uint first;
    uint second;
    uint sum;
    // Which cells are inside, and the reflective shadow map: its view, depth and diffuse colour.
    uint inside;
    uint mapView;
    uint mapDepth;
    uint mapAlbedo;
    // The map's normals, the interior boxes and how many, and the pixels' light at half size.
    uint mapNormal;
    uint interiors;
    uint interiorCount;
    uint output;
    uint halfWidth;
    uint halfHeight;
    // A scale on the light that the pixels read.
    float brightness;
    uint padding;
};

// The coefficients of the 4 spherical harmonics of the first two bands, in the order 1, x, y, z.
static const float ShBand0 = 0.282094792;
static const float ShBand1 = 0.488602512;

// The coefficients of a clamped cosine lobe about a direction, the same order.
static const float LobeBand0 = 0.886226925;
static const float LobeBand1 = 1.023326707;

// The solid angles, in steradians, that a face of a cell covers seen from the centre of its neighbour: the face
// straight ahead, and each of the 4 faces at its sides.
static const float AheadFaceAngle = 0.4006696846;
static const float SideFaceAngle = 0.4234413544;

// The most reflective shadow map texels that a cell reads across each side.
static const uint MostInjectTaps = 8u;

// A surface's light starts half a cell in front of it, so it does not light itself through its own cell.
static const float InjectOffset = 0.5;

// The metres in front of a surface at which a pixel finds whether it is inside: past the error of its depth, and less
// than the thinnest wall.
static const float InsideOffset = 0.05;

VolumeConstants LoadVolumeConstants() { return LoadConstants<VolumeConstants>(GetPassHeader().y); }

// The unit vector along axis 0, 1 or 2: x, y or z.
float3 GetAxis(uint axis) { return float3(axis == 0u ? 1.0 : 0.0, axis == 1u ? 1.0 : 0.0, axis == 2u ? 1.0 : 0.0); }

float4 GetSh(float3 d) { return float4(ShBand0, ShBand1 * d.x, ShBand1 * d.y, ShBand1 * d.z); }

float4 GetLobe(float3 d) { return float4(LobeBand0, LobeBand1 * d.x, LobeBand1 * d.y, LobeBand1 * d.z); }

// A cell's light, red, green and blue, from 3 texels along x of a volume.
struct CellLight
{
    float4 r;
    float4 g;
    float4 b;
};

CellLight LoadCell(uint id, uint3 cell)
{
    StorageVolumeRef volume = { id, 0u };
    uint3 at = uint3(cell.x * 3u, cell.y, cell.z);
    CellLight light = { volume.Load(at), volume.Load(at + uint3(1u, 0u, 0u)), volume.Load(at + uint3(2u, 0u, 0u)) };
    return light;
}

void StoreCell(uint id, uint3 cell, CellLight light)
{
    StorageVolumeRef volume = { id, 0u };
    uint3 at = uint3(cell.x * 3u, cell.y, cell.z);
    volume.Store(at, light.r);
    volume.Store(at + uint3(1u, 0u, 0u), light.g);
    volume.Store(at + uint3(2u, 0u, 0u), light.b);
}

float3 GetCellCenter(VolumeConstants constants, uint3 cell)
{
    return constants.origin + (float3(cell) + 0.5) * constants.cell;
}

// Whether a camera-relative point is in an interior box of the view.
bool IsInside(VolumeConstants constants, float3 point)
{
    return IsInInterior(constants.interiors, constants.interiorCount, point);
}

// Whether a cell is inside, as the find of the inside stored it.
bool IsCellInside(VolumeConstants constants, uint3 cell)
{
    StorageVolumeRef inside = { constants.inside, 0u };
    return inside.Load(cell).x > 0.5;
}

// The light of each cell from the texels of the reflective shadow map in it.
[shader("compute")]
[numthreads(4, 4, 4)]
void InjectLight<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    VolumeConstants constants = LoadVolumeConstants();
    if (any(thread >= constants.size))
        return;

    float3 center = GetCellCenter(constants, thread);
    bool isInside = IsInside(constants, center);
    float halfCell = 0.5 * constants.cell;
    GpuShadowView view = LoadShadowView(constants.mapView, 0u);
    TextureRef mapDepth = { constants.mapDepth };
    TextureRef mapAlbedo = { constants.mapAlbedo };
    TextureRef mapNormal = { constants.mapNormal };
    float2 size = float2(mapDepth.GetSize(0u));
    float4 clip = mul(float4(center, 1.0), view.viewProjection);
    float2 texel = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5) * size;

    // The cell's square on the map, in texels, about its centre: wide enough for its diagonal.
    float reach = 0.87 * constants.cell / view.texelSize;
    float stride = max(2.0 * reach / float(MostInjectTaps), 1.0);
    uint taps = min(uint(ceil(2.0 * reach / stride)), MostInjectTaps);
    Lights lights = GetLights();
    float3 sun = GetSunIlluminance<S>(lights);
    // Each texel stands for the surface under its square, which the sun lights with its whole light on that square.
    float area = view.texelSize * view.texelSize * stride * stride;
    CellLight light = { float4(0.0), float4(0.0), float4(0.0) };
    for (uint y = 0u; y < taps; y++)
    {
        for (uint x = 0u; x < taps; x++)
        {
            float2 at = texel - reach + (float2(x, y) + 0.5) * stride;
            if (any(at < 0.0) || any(at >= size))
                continue;

            uint2 read = uint2(at);
            float depth = mapDepth.Load(read, 0u).x;
            if (depth == 0.0)
                continue;

            float2 ndc = (float2(read) + 0.5) / size * float2(2.0, -2.0) + float2(-1.0, 1.0);
            float4 lit = mul(float4(ndc, depth, 1.0), constants.clipToRelative);
            float3 normal = NormalizeSafe(mapNormal.Load(read, 0u).xyz * 2.0 - 1.0, float3(0.0, 1.0, 0.0));
            float3 source = lit.xyz / lit.w + normal * (InjectOffset * constants.cell);
            if (any(abs(source - center) > halfCell) || IsInside(constants, source) != isInside)
                continue;

            float3 flux = mapAlbedo.Load(read, 0u).rgb * sun * area;
            float4 lobe = GetLobe(normal) * InvPi;
            light.r += flux.r * lobe;
            light.g += flux.g * lobe;
            light.b += flux.b * lobe;
        }
    }

    StoreCell(constants.first, thread, light);
    StoreCell(constants.sum, thread, light);
}

// Which cells are inside: 1 where the cell's centre is in an interior box, 0 where it is not.
[shader("compute")]
[numthreads(4, 4, 4)]
void FindInside(uint3 thread : SV_DispatchThreadID)
{
    VolumeConstants constants = LoadVolumeConstants();
    if (any(thread >= constants.size))
        return;

    StorageVolumeRef volume = { constants.inside, 0u };
    volume.Store(thread, float4(IsInside(constants, GetCellCenter(constants, thread)) ? 1.0 : 0.0));
}

// The light that a neighbour's light sends through one face of a cell: its intensity towards the face times the face's
// solid angle is the flux through it, which leaves the face as a small light facing out of it.
void AddFace(inout CellLight light, CellLight from, float3 toward, float3 outward, float angle)
{
    float4 sh = GetSh(toward);
    float3 intensity = max(float3(dot(from.r, sh), dot(from.g, sh), dot(from.b, sh)), 0.0);
    float4 lobe = GetLobe(outward) * (angle * InvPi);
    light.r += intensity.r * lobe;
    light.g += intensity.g * lobe;
    light.b += intensity.b * lobe;
}

// One step of the propagation: each cell gathers what its 6 neighbours send through its 5 faces that each sees, from
// the neighbours that are inside as it is.
[shader("compute")]
[numthreads(4, 4, 4)]
void PropagateLight(uint3 thread : SV_DispatchThreadID)
{
    VolumeConstants constants = LoadVolumeConstants();
    if (any(thread >= constants.size))
        return;

    bool isOdd = (GetPassHeader().z & 1u) != 0u;
    uint source = isOdd ? constants.second : constants.first;
    uint target = isOdd ? constants.first : constants.second;
    bool isInside = IsCellInside(constants, thread);
    int3 cell = int3(thread);
    int size = int(constants.size);
    CellLight light = { float4(0.0), float4(0.0), float4(0.0) };
    for (uint n = 0u; n < 6u; n++)
    {
        uint axis = n >> 1u;
        float sign = (n & 1u) != 0u ? -1.0 : 1.0;
        int3 offset = int3(GetAxis(axis) * sign);
        // The light moves from the neighbour behind the cell along the direction.
        int3 neighbour = cell - offset;
        if (any(neighbour < 0) || any(neighbour >= size) || IsCellInside(constants, uint3(neighbour)) != isInside)
            continue;

        float3 forward = float3(offset);
        CellLight from = LoadCell(source, uint3(neighbour));
        AddFace(light, from, forward, forward, AheadFaceAngle);
        for (uint s = 0u; s < 4u; s++)
        {
            uint sideAxis = (axis + 1u + (s >> 1u)) % 3u;
            float3 side = GetAxis(sideAxis) * ((s & 1u) != 0u ? -1.0 : 1.0);
            AddFace(light, from, normalize(forward + 0.5 * side), side, SideFaceAngle);
        }
    }

    StoreCell(target, thread, light);
    CellLight sum = LoadCell(constants.sum, thread);
    sum.r += light.r;
    sum.g += light.g;
    sum.b += light.b;
    StoreCell(constants.sum, thread, sum);
}

// The sum's light that reaches a surface facing normal at a camera-relative point, as the irradiance over pi that a
// white diffuse surface reflects: the intensity that comes towards the surface over the cell's face. Of the 8 cells
// around it, it reads only those that are inside as the point is, weighted again to a sum of 1.
float3 ReadSum(VolumeConstants constants, float3 point, float3 normal)
{
    bool isInside = IsInside(constants, point + normal * InsideOffset);
    float3 at = (point + normal * (0.5 * constants.cell) - constants.origin) / constants.cell - 0.5;
    float3 last = float3(constants.size) - 1.0;
    if (any(at < 0.0) || any(at > last))
        return float3(0.0);

    uint3 first = uint3(min(floor(at), last - 1.0));
    float3 f = at - float3(first);
    float4 lobe = GetLobe(-normal);
    float3 sum = float3(0.0);
    float weights = 0.0;
    for (uint i = 0u; i < 8u; i++)
    {
        uint3 corner = uint3(i & 1u, (i >> 1u) & 1u, i >> 2u);
        if (IsCellInside(constants, first + corner) != isInside)
            continue;

        float3 along = lerp(1.0 - f, f, float3(corner));
        float weight = along.x * along.y * along.z;
        CellLight light = LoadCell(constants.sum, first + corner);
        float3 coming = float3(dot(light.r, lobe), dot(light.g, lobe), dot(light.b, lobe));
        sum += max(coming, 0.0) * weight;
        weights += weight;
    }

    if (weights <= Epsilon)
        return float3(0.0);

    return sum / weights / (constants.cell * constants.cell) * InvPi * constants.brightness;
}

// The bounce light of each pixel at half size.
[shader("compute")]
[numthreads(8, 8, 1)]
void LightPixels(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    VolumeConstants constants = LoadVolumeConstants();
    if (any(thread.xy >= uint2(constants.halfWidth, constants.halfHeight)))
        return;

    StorageRef output = { constants.output, 0u };
    uint2 pixel = min(thread.xy * 2u, uint2(frame.width, frame.height) - 1u);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
    {
        output.Store(thread.xy, float4(0.0));
        return;
    }

    float3 point = ReconstructPixel(frame, pixel, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    output.Store(thread.xy, float4(ReadSum(constants, point, normal), 0.0));
}
