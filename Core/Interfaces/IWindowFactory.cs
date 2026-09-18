namespace Magic.Interfaces;

public interface IWindowFactory
{
    IWindow Create(string title, int width, int height, WindowMode mode);
}
