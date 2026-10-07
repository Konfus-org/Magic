using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Magic.Contexts.Settings;

/// <summary>
/// How readily detail is given up, which everything that picks a level of detail agrees on: the renderer for a
/// model's LODs, streaming for a chunk's stand-ins. Read every frame.
/// </summary>
[Settings("Lod")]
[Category("World/Detail")]
public sealed class LodSettings
{
    /// <summary>
    /// How readily a lesser version (a LOD) is used in place of the full one: 1 is at the thresholds the LODs were
    /// made for; above 1 keeps detail longer (2 switches a model at half the size on screen, a chunk at twice the
    /// distance), below 1 gives it up sooner.
    /// </summary>
    [DisplayName("Level of detail bias")]
    [Description("Above 1 keeps full detail longer (2 switches a model at half the size on screen), below 1 gives it up sooner.")]
    [Range(0.1, 4)]
    public float Bias { get; set; } = 1f;
}
