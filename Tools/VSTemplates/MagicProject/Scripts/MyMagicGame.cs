using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;

namespace MyMagicGame;

/// <summary>
/// The project's gem: constructed when the engine opens the project, disposed when it closes or the gem is rebuilt
/// (rebuild while the engine runs and it hot reloads). Constructor parameters are what it depends on: host services
/// (Project, Events, IFileSystem, Threads, Settings) or what the engine's gems provide, such as IEcs, Assets or World. Every
/// handle taken from them is disposed here; the host tracks none.
/// </summary>
internal sealed class MyMagicGame : IGem
{
    private readonly IEcsQuery<Transform> _moving;
    private readonly QueryEachAction<Transform> _move; // one delegate, not one per frame

    public MyMagicGame(IEcs ecs)
    {
        _moving = ecs.Query<Transform>().Build();
        _move = Move;
    }

    public void Dispose()
    {
        _moving.Dispose();
    }

    /// <summary>
    /// Once a frame. What <see cref="Move"/> needs of the frame (frame.Delta, in seconds) goes in a private property set
    /// here, so no closure is made each frame.
    /// </summary>
    public void Update(in Frame frame)
    {
        _moving.Each(_move);
    }

    private void Move(Handle entity, ref Transform transform)
    {
        // Move things here. A component is any struct implementing IComponent; chunk files name it by its type name.
    }
}
