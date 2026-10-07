using Flecs.NET.Core;
using Magic.Interfaces;
using FlecsEntity = Flecs.NET.Core.Entity;
using FlecsWorldHandle = Flecs.NET.Core.World;

namespace FlecsEcsGem;

/// <summary>
/// One flecs phase tag and one pipeline per <see cref="UpdateType"/>. A pipeline is a query over systems; running
/// it runs every enabled system tagged with its phase, in scheduling order.
/// </summary>
internal sealed class Phases
{
    private readonly ulong[] _tags = new ulong[Enum.GetValues<UpdateType>().Length];
    private readonly ulong[] _pipelines = new ulong[Enum.GetValues<UpdateType>().Length];

    public Phases(FlecsWorldHandle world)
    {
        foreach (UpdateType phase in Enum.GetValues<UpdateType>())
        {
            FlecsEntity tag = world.Entity($"Magic.{phase}").Add(Ecs.Phase);
            _tags[(int)phase] = tag.Id;

            _pipelines[(int)phase] = world.Pipeline($"Magic.{phase}Pipeline")
                .With(Ecs.System)
                .With(tag.Id)
                .Build().Entity.Id;
        }
    }

    public ulong Tag(UpdateType phase)
    {
        return _tags[(int)phase];
    }

    public ulong Pipeline(UpdateType phase)
    {
        return _pipelines[(int)phase];
    }
}
