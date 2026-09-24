using Magic.Contexts;
using Magic.Contexts.Assets;
using System.Drawing;

namespace Magic.Interfaces;

public enum WindowMode
{
    Windowed,
    Fullscreen,
    Borderless
}

public interface IWindow : IDisposable
{
    bool IsOpen { get; }

    /// <summary>The window's id, as the windowing backend reports it in its events.</summary>
    uint Handle { get; }

    string Title { get; set; }
    Handle<Texture> Icon { get; set; }
    Size Size { get; set; }
    WindowMode Mode { get; set; }

    void Show();
    void Hide();
    void Minimize();
    void Maximize();
}
