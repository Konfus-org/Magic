using Core.Interfaces;
using DryIoc;

namespace HelloWorld;

internal sealed class HelloWorld : IGem
{
    public void OnLoad(IContainer services)
    {
        // A gem providing e.g. a renderer would do: services.Register<IRenderer, VulkanRenderer>();
        Console.WriteLine("Hello World!");
    }

    public void OnReloaded(byte[] restore)
    {
        Console.WriteLine("Reloaded Hello World!");
    }

    public void OnReloading(byte[] persist)
    {
        Console.WriteLine("Reloading Hello World!");
    }

    public void OnUnload()
    {
        Console.WriteLine("Goodbye World!");
    }
}
