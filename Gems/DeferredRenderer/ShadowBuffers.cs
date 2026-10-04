using System.Drawing;

namespace DeferredRendererGem;

/// <summary>
/// What a perspective view has while the sun casts shadows: its cascades as last fitted (a staggered far cascade keeps
/// its fit for a frame), which row of the atlas their tiles sit in, and which were drawn again this frame.
/// </summary>
internal sealed class ShadowBuffers
{
    public Cascade[] Cascades { get; } = new Cascade[DeferredRendererGem.Cascades.MaxCascades];

    /// <summary>
    /// How many cascades this frame; 0 when the view has none (no sun casts, or shadows are off).
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// How many of <see cref="Cascades"/> hold a fit from some frame: a cascade past this has never been fitted.
    /// </summary>
    public int Fitted { get; set; }

    public int Row { get; set; }

    /// <summary>
    /// Bit i set when cascade i was drawn again this frame.
    /// </summary>
    public uint Refreshed { get; set; }

    public Rectangle Tile(int cascade, int resolution)
    {
        return DeferredRendererGem.Cascades.Tile(cascade, Row, resolution);
    }
}
