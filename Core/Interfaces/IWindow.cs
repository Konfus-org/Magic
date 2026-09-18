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
    string Title { get; set; }
    Guid Icon { get; set; }
    Size Size { get; set; }
    WindowMode Mode { get; set; }
    bool IsOpen { get; }

    void Show();
    void Hide();
    void Minimize();
    void Maximize();
}
