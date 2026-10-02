using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using System.Drawing;

namespace Magic.UnitTests.Fakes;

/// <summary>One open 800 x 600 window, the main one.</summary>
internal sealed class FakeWindows : IWindowRegistry, IWindow
{
    public IWindow? Main => this;
    public IReadOnlyList<IWindow> Windows => [this];

    public void Dispose() { }
    public IWindow? Get(uint handle) => handle == Handle ? this : null;

    public bool IsOpen => true;
    public uint Handle => 1;
    public string Title { get; set; } = "";
    public Handle<Texture> Icon { get; set; }
    public Size Size { get; set; } = new(800, 600);
    public Size PixelSize => Size;
    public WindowMode Mode { get; set; }
    public void Show() { }
    public void Hide() { }
    public void Minimize() { }
    public void Maximize() { }
}
