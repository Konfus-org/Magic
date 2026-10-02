using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using System.Numerics;

namespace Magic.Contexts.Rendering;

/// <summary>
/// A camera to draw a frame from: the component (which says the <see cref="RenderTarget"/>), its world matrix, and the
/// passes its entity's <see cref="PostProcessing"/> lists (none without one).
/// </summary>
internal readonly record struct View(Camera Camera, Matrix4x4 World, PassList Passes);

internal enum LightKind : byte
{
    Directional,
    Point,
    Spot
}

/// <summary>One light of any kind with its entity's world matrix; the fields a kind does not have are zero.</summary>
internal readonly record struct LightInstance(LightKind Kind, Vector3 Color, float Intensity, float Range, float InnerAngle, float OuterAngle, bool CastsShadows, Matrix4x4 World);

/// <summary>Everything drawing one entity takes; <see cref="Static"/> promises the world matrix never changes.</summary>
internal readonly record struct InstanceDesc(Handle<Model> Model, MaterialSlots Materials, RenderFlags Flags, Matrix4x4 World, bool Static);

/// <summary>
/// What the last frame did. <see cref="CpuRecordMs"/> is main-thread time recording and submitting, not counting
/// <see cref="CpuWaitMs"/>, the time blocked on the GPU (an older frame, or a swapchain image to draw into).
/// </summary>
internal readonly record struct RenderStats(
    uint Instances,
    uint Draws,
    uint Dispatches,
    uint PipelinesPending,
    uint ResidentMeshes,
    uint ResidentTextures,
    float CpuSyncMs,
    float CpuRecordMs,
    float CpuWaitMs);
