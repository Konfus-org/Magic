using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Drawing;
using System.Numerics;

namespace Samples.GracefulFailures;

/// <summary>
/// Says under its entity, in the world, which failure it shows and why that happens: two short lines, the title in
/// the failure's colour and the reason under it. Drawn whenever the debug text is (always in a Debug build).
/// </summary>
internal sealed class Caption(Handle entity, IEcs ecs, IDebugUI debug) : IBehavior
{
    /// <summary>
    /// What failed. The chunk sets these beside the script's id.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Why: a few words, the log has the rest.
    /// </summary>
    public string Why { get; set; } = "";

    /// <summary>
    /// Metres under the entity's origin the title is drawn at.
    /// </summary>
    public float Below { get; set; } = 0.9f;

    public void Update(in Frame frame)
    {
        Vector3 under = ecs.Get<Transform>(entity).Position - new Vector3(0, Below, 0);
        debug.Text(under, Title, Color.Orange);
        debug.Text(under - new Vector3(0, 0.3f, 0), Why, Color.White);
    }
}
