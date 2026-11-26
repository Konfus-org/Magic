using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// How much of each light reaches each pixel this frame, the colour the sun's light takes on through glass, and the
/// sun's shadow maps it was read from with how it was read.
/// </summary>
/// <remarks>
/// The mask has the layout <c>Scene/Shadows.slang</c> reads. The tint is none where no glass tints the light, and the
/// cascade atlas and the sun's details none where no shadow map made the mask. The details hold, for each pixel, the
/// sun's penumbra over <see cref="WidestPenumbra"/> texels, its cascade and its contact shadow, which the
/// <see cref="DebugView.Penumbra"/>, <see cref="DebugView.ShadowCascades"/> and <see cref="DebugView.ContactShadows"/>
/// views show. The <see cref="DebugView.CascadeAtlas"/> view shows the atlas. Their ids are in the frame's buffer as
/// <see cref="GpuShadows"/>.
/// </remarks>
public readonly record struct ShadowMask(
    GpuBuffer Mask,
    GpuTexture SunTint,
    GpuTexture CascadeAtlas,
    GpuTexture SunDetails
)
{
    /// <summary>
    /// The widest penumbra in texels that the details' penumbra is a share of, the twin of <c>Scene/Shadows.slang</c>'s.
    /// </summary>
    public const float WidestPenumbra = 32;
}
