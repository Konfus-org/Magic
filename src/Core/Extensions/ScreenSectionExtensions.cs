using Magic.Contexts.ECS;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace Magic.Extensions;

/// <summary>
/// Where a <see cref="ScreenSection"/> is in a window, so whatever draws a camera and whatever draws over its view agree.
/// </summary>
public static class ScreenSectionExtensions
{
    extension(ScreenSection section)
    {
        /// <summary>
        /// The section's rectangle of a window of <paramref name="size"/>, in pixels: halves and quarters split at the
        /// middle, the odd pixel going right and down.
        /// </summary>
        public Rectangle AreaIn(Size size)
        {
            int halfWidth = size.Width / 2;
            int halfHeight = size.Height / 2;
            return section switch
            {
                ScreenSection.TopLeft => new Rectangle(0, 0, halfWidth, halfHeight),
                ScreenSection.TopRight => new Rectangle(halfWidth, 0, size.Width - halfWidth, halfHeight),
                ScreenSection.BottomLeft => new Rectangle(0, halfHeight, halfWidth, size.Height - halfHeight),
                ScreenSection.BottomRight => new Rectangle(halfWidth, halfHeight, size.Width - halfWidth, size.Height - halfHeight),
                ScreenSection.Top => new Rectangle(0, 0, size.Width, halfHeight),
                ScreenSection.Bottom => new Rectangle(0, halfHeight, size.Width, size.Height - halfHeight),
                ScreenSection.Left => new Rectangle(0, 0, halfWidth, size.Height),
                ScreenSection.Right => new Rectangle(halfWidth, 0, size.Width - halfWidth, size.Height),
                ScreenSection.Full => new Rectangle(Point.Empty, size),
                _ => new Rectangle(Point.Empty, size),
            };
        }
    }
}
