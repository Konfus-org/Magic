using System.Numerics;
using Magic.Attributes;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// A few words of debug text over its entity, naming what is there, or, with <see cref="IsOnScreen"/>, lines across
/// the top of the screen, as a sample's keys are: <c>{ "id": 3450, "text": "Parallel system" }</c>. Impatient, so the
/// loading domain's word shows while it loads; it only draws, so it is safe before the world is there.
/// </summary>
[Impatient]
internal sealed class Label(Entity owner, World world, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// What it says; on screen, each line of it a line.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Metres above its entity the words stand.
    /// </summary>
    public float Height { get; set; } = 1.5f;

    /// <summary>
    /// Whether it is lines across the screen rather than words in the world.
    /// </summary>
    public bool IsOnScreen { get; set; }

    public void Update(in Frame frame)
    {
        if (debugUI is not { Enabled: true } || Text.Length == 0) return;

        if (!IsOnScreen)
        {
            debugUI.Text(world.Get<Transform>(owner).Position + (Vector3.UnitY * Height), Text);
            return;
        }

        foreach (string line in Text.Split('\n'))
        {
            debugUI.Text(line);
        }
    }
}
