namespace DeferredRendererGem;

/// <summary>
/// How a view's depth pyramid is laid out: a buffer of floats, level after level, level 0 at half the view's resolution
/// (sizes rounded up), each level the farthest depth of the texels it covers, down to one texel or
/// <see cref="MaxLevels"/> levels. The frame constants carry the size so the cull shaders find a level, and the
/// <c>HiZBuild</c> pass sizes its buffer by the float count (<c>$hiZFloats</c>).
/// </summary>
internal static class HiZ
{
    public const int MaxLevels = 12;

    /// <summary>
    /// The pyramid of a view of this size: level 0's size, how many levels, and the floats they take together.
    /// </summary>
    public static (int Width, int Height, int Levels, uint Floats) Size(int viewWidth, int viewHeight)
    {
        int width = Math.Max(1, (viewWidth + 1) / 2), height = Math.Max(1, (viewHeight + 1) / 2);
        int levels = 1;
        uint floats = 0;
        int levelWidth = width, levelHeight = height;
        while (true)
        {
            floats += (uint)(levelWidth * levelHeight);
            if ((levelWidth == 1 && levelHeight == 1) || levels == MaxLevels)
                break;

            levelWidth = (levelWidth + 1) / 2;
            levelHeight = (levelHeight + 1) / 2;
            levels++;
        }

        return (width, height, levels, floats);
    }
}
