using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Attributes;
using Magic.Interfaces;
using static Magic.Box3DPhysics.B3;

namespace Magic.Box3DPhysics;

/// <summary>
/// How the physics world is stepped. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set Box3DPhysics.gravity=1.62</c>.
/// </summary>
[Category("Scripting/Physics")]
internal sealed class Box3DPhysicsSettings : ISettings
{
    /// <summary>
    /// Metres per second squared that everything dynamic falls at, straight down; 0 leaves everything floating.
    /// </summary>
    public float Gravity { get; set; } = 9.81f;

    /// <summary>
    /// Metres from the nearest of the player's cameras within which a body is simulated; a body farther off is dropped,
    /// its entity staying put, and made again when the player comes near. With no player camera, everything is.
    /// </summary>
    public float Range { get; set; } = 200;

    /// <summary>
    /// Solver passes in each fixed step: more keeps stacks and joints steadier and costs as much more time.
    /// </summary>
    [Range(1, 16)]
    public int Substeps { get; set; } = 4;

    /// <summary>
    /// Threads the solver runs on, its own; 1 solves on the step's thread alone, which small worlds are best with.
    /// </summary>
    [RequireRestart]
    [Range(1, B3_MAX_WORKERS)]
    public int Workers { get; set; } = 1;
}
