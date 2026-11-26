using System.ComponentModel;
using Magic.Configuration;

namespace Magic.ShadowMaps;

/// <summary>
/// How the shadow maps are drawn and read.
/// </summary>
[Category("Rendering")]
public sealed class ShadowMapsSettings : ISettings
{
    /// <summary>
    /// Whether shadow maps are drawn; off, every light reaches every surface.
    /// </summary>
    public bool IsShadowMapsOn { get; set; } = true;

    /// <summary>
    /// How many cascades the sun's shadow is split into along the view, from 1 to 4. More keep near shadows sharp.
    /// </summary>
    public int CascadeCount { get; set; } = 4;

    /// <summary>
    /// The texels along each side of one cascade's shadow map. The cascades share an atlas of two by two of them.
    /// </summary>
    public int CascadeResolution { get; set; } = 2048;

    /// <summary>
    /// How the cascades split the view, from 0 to 1: 0 splits it into even lengths, 1 so each cascade reaches the same
    /// number of times further than the one before (Zhang and others, GPU Gems 3, ch. 10).
    /// </summary>
    public float SplitLambda { get; set; } = 0.5f;

    /// <summary>
    /// The metres from the camera to which the sun casts shadows.
    /// </summary>
    public float MaxShadowDistance { get; set; } = 150;

    /// <summary>
    /// How far a surface moves along its normal before it is tested, in texels of its cascade at a glancing light. It
    /// is half a texel where the light faces the surface. More removes acne and pulls the shadow off its caster.
    /// </summary>
    public float NormalBias { get; set; } = 1.5f;

    /// <summary>
    /// The share of a cascade's side, at its edge, over which it blends into the next, from 0 to 0.5.
    /// </summary>
    public float CascadeBlend { get; set; } = 0.1f;

    /// <summary>
    /// The taps that look for the casters in front of a point, which set how soft its shadow is.
    /// </summary>
    public int BlockerTaps { get; set; } = 16;

    /// <summary>
    /// The taps that filter a shadow over its penumbra. More make a wide penumbra smoother.
    /// </summary>
    public int FilterTaps { get; set; } = 32;

    /// <summary>
    /// The half width of the widest penumbra, in texels of its shadow map, from 1 to 32. A caster further from its
    /// shadow than this allows gives a shadow no softer.
    /// </summary>
    public float MaxPenumbraTexels { get; set; } = 32;

    /// <summary>
    /// Whether a short march through the depth buffer towards the light shadows the gap that the shadow map leaves
    /// where a caster meets its shadow.
    /// </summary>
    public bool IsContactShadowsOn { get; set; } = true;

    /// <summary>
    /// The metres that the contact shadow's march goes towards the light.
    /// </summary>
    public float ContactShadowLength { get; set; } = 0.2f;

    /// <summary>
    /// The steps of the contact shadow's march, from 1 to 64.
    /// </summary>
    public int ContactShadowSteps { get; set; } = 12;

    /// <summary>
    /// The metres behind the depth buffer that a step of the march still counts as hidden, so a thin surface far
    /// behind another does not shadow.
    /// </summary>
    public float ContactShadowThickness { get; set; } = 0.05f;
}
