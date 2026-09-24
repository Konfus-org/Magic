using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>One captured frame: 8-bit RGBA, rows tightly packed top to bottom, <c>Width * Height * 4</c> bytes.</summary>
public readonly record struct Frame(int Width, int Height, byte[] Pixels);

/// <summary>
/// Reads back what a window last presented. Exported by whichever gem owns the swapchain (the renderer);
/// the host asks for it only when a screenshot is due, so a run without one merely logs that the
/// screenshot was skipped. The host writes the file: a capture is pixels, not a format.
/// </summary>
public interface IFrameCapture
{
    /// <summary>The last frame presented to the window with this <see cref="IWindow.Handle"/>; failed, with why, when there is none.</summary>
    Result<Frame> Capture(uint window);
}
