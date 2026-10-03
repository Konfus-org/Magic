using Magic.Contexts.Components;
using System.Drawing;

namespace Magic.Extensions;

public static class ViewportRegionExtensions
{
    extension(ViewportRegion region)
    {
        /// <summary>
        /// The pixels of a <paramref name="width"/> × <paramref name="height"/> target the region covers, origin top left.
        /// </summary>
        public Rectangle ToPixels(int width, int height)
        {
            int hw = width / 2, hh = height / 2;

            return region switch
            {
                ViewportRegion.TopLeft => new Rectangle(0, 0, hw, hh),
                ViewportRegion.TopRight => new Rectangle(hw, 0, width - hw, hh),
                ViewportRegion.BottomLeft => new Rectangle(0, hh, hw, height - hh),
                ViewportRegion.BottomRight => new Rectangle(hw, hh, width - hw, height - hh),
                ViewportRegion.TopHalf => new Rectangle(0, 0, width, hh),
                ViewportRegion.BottomHalf => new Rectangle(0, hh, width, height - hh),
                ViewportRegion.LeftHalf => new Rectangle(0, 0, hw, height),
                ViewportRegion.RightHalf => new Rectangle(hw, 0, width - hw, height),
                _ => new Rectangle(0, 0, width, height),
            };
        }
    }
}
