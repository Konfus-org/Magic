using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>
/// Scheduled systems: which frame hook runs them, with what delta, in what order.
/// </summary>
public sealed class FlecsSystemTests
{
    [Fact]
    public void A_system_runs_on_its_phase()
    {
        using FlecsEcs ecs = new();
        int runs = 0;
        using IDisposable system = ecs.Schedule("Count").On(UpdateType.FixedUpdate).Run(_ => runs++);

        ecs.FixedUpdate(FrameOf(1f));

        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_system_does_not_run_outside_its_phase()
    {
        using FlecsEcs ecs = new();
        int runs = 0;
        using IDisposable system = ecs.Schedule("Count").On(UpdateType.FixedUpdate).Run(_ => runs++);

        ecs.Update(FrameOf(1f));

        Assert.Equal(0, runs);
    }

    [Fact]
    public void A_system_is_handed_the_frames_delta()
    {
        using FlecsEcs ecs = new();
        float delta = 0f;
        using IDisposable system = ecs.Schedule("Delta").Run(dt => delta = dt);

        ecs.Update(FrameOf(0.5f));

        Assert.Equal(0.5f, delta);
    }

    [Fact]
    public void A_system_without_a_query_sees_its_own_changes_at_once()
    {
        using FlecsEcs ecs = new();
        float seen = 0f;
        using IDisposable system = ecs.Schedule("Spawn").Run(_ =>
        {
            Handle entity = ecs.Create();
            ecs.Set(entity, new Position { X = 3 });
            seen = ecs.Get<Position>(entity).X;
        });

        ecs.Update(FrameOf(1f));

        Assert.Equal(3f, seen);
    }

    [Fact]
    public void Systems_on_one_phase_run_in_the_order_they_were_scheduled()
    {
        using FlecsEcs ecs = new();
        List<string> order = [];
        using IDisposable first = ecs.Schedule("First").Run(_ => order.Add("first"));
        using IDisposable second = ecs.Schedule("Second").Run(_ => order.Add("second"));

        ecs.Update(FrameOf(1f));

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void A_query_system_visits_the_matching_entities()
    {
        using FlecsEcs ecs = new();
        Handle entity = ecs.Create();
        ecs.Set(entity, new Position());
        ecs.Set(entity, new Velocity { X = 4 });
        using IDisposable system = ecs.Schedule("Move").Query<Position, Velocity>()
            .Each((float dt, Handle _, ref Position position, ref Velocity velocity) => position.X += velocity.X * dt);

        ecs.Update(FrameOf(0.5f));

        Assert.Equal(2, ecs.Get<Position>(entity).X);
    }

    [Fact]
    public void A_parallel_system_visits_every_entity_once()
    {
        using FlecsEcs ecs = new();
        Handle[] ids = new Handle[10_000];
        using (ecs.Group())
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = ecs.Create();
                ecs.Set(ids[i], new Position());
            }
        }

        int visits = 0;
        using IDisposable system = ecs.Schedule("Count").Query<Position>().Parallel()
            .Each((float _, Handle _, ref Position _) => Interlocked.Increment(ref visits));

        ecs.Update(FrameOf(1f));

        Assert.Equal(ids.Length, visits);
    }

    [Fact]
    public void A_disposed_system_no_longer_runs()
    {
        using FlecsEcs ecs = new();
        int runs = 0;
        IDisposable system = ecs.Schedule("Count").Run(_ => runs++);

        system.Dispose();
        ecs.Update(FrameOf(1f));

        Assert.Equal(0, runs);
    }

    private static Frame FrameOf(float delta)
    {
        return new Frame(1, 0, delta, default, new RenderCommands());
    }
}
