using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;

namespace MyMagicGame;

/// <summary>
/// The project's gem: constructed when the engine opens the project, disposed when it closes or the gem is rebuilt
/// (rebuild while the engine runs and it hot reloads). Constructor parameters are what it depends on: host services
/// (Project, Assets, Events, IFileSystem, Scheduler, World) or interfaces other gems provide, such as IEcs from the ECS gem. Every
/// handle taken from them is disposed here; the host tracks none.
/// </summary>
internal sealed class MyMagicGame(IEcs ecs) : IGem
{
    private readonly IEcsQuery<Transform> _moving = ecs.Query<Transform>().Build();

    public void Dispose()
    {
        _moving.Dispose();
    }

    /// <summary>Once a frame; frame.Delta is in seconds.</summary>
    public void Update(in Frame frame)
    {
        float delta = frame.Delta;
        _moving.Each((Handle entity, ref Transform transform) =>
        {
            // Move things here, using delta. A component is any struct implementing IComponent; chunk files name it by its type name.
        });
    }
}
