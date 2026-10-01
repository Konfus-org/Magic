using FlecsGem;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>Systems added to the scheduler, run by the real Flecs gem's frame hooks.</summary>
public sealed class SchedulerTests : IDisposable
{
    private readonly FlecsEcs _ecs = new();
    private readonly Scheduler _scheduler = new();

    public void Dispose()
    {
        _ecs.Dispose();
    }

    [Fact]
    public void An_added_system_runs_on_its_phase()
    {
        FirstSystem system = new([], UpdateType.FixedUpdate);
        using IDisposable scheduled = _scheduler.Add(_ecs,system);

        _ecs.FixedUpdate(FrameOf(1f));

        Assert.Single(system.Frames);
    }

    [Fact]
    public void An_added_system_does_not_run_outside_its_phase()
    {
        FirstSystem system = new([], UpdateType.FixedUpdate);
        using IDisposable scheduled = _scheduler.Add(_ecs,system);

        _ecs.Update(FrameOf(1f));

        Assert.Empty(system.Frames);
    }

    [Fact]
    public void A_system_is_handed_the_frame_that_was_begun()
    {
        FirstSystem system = new([]);
        using IDisposable scheduled = _scheduler.Add(_ecs,system);
        _scheduler.SetFrame(new Frame(7, 0, 1f, default));

        _ecs.Update(FrameOf(1f));

        Assert.Equal(7, Assert.Single(system.Frames).Number);
    }

    [Fact]
    public void A_system_is_handed_the_delta_of_its_phase()
    {
        FirstSystem system = new([], UpdateType.FixedUpdate);
        using IDisposable scheduled = _scheduler.Add(_ecs,system);
        _scheduler.SetFrame(FrameOf(1f));

        _ecs.FixedUpdate(FrameOf(0.25f));

        Assert.Equal(0.25f, Assert.Single(system.Frames).Delta);
    }

    [Fact]
    public void Systems_on_one_phase_run_in_the_order_they_were_added()
    {
        List<string> order = [];
        using IDisposable first = _scheduler.Add(_ecs,new FirstSystem(order));
        using IDisposable second = _scheduler.Add(_ecs,new SecondSystem(order));

        _ecs.Update(FrameOf(1f));

        Assert.Equal(["FirstSystem", "SecondSystem"], order);
    }

    [Fact]
    public void A_system_whose_handle_was_disposed_no_longer_runs()
    {
        FirstSystem system = new([]);
        IDisposable scheduled = _scheduler.Add(_ecs,system);

        scheduled.Dispose();
        _ecs.Update(FrameOf(1f));

        Assert.Empty(system.Frames);
    }

    private static Frame FrameOf(float delta)
    {
        return new Frame(1, 0, delta, default);
    }

    /// <summary>Notes its name in <paramref name="order"/> and keeps every frame it was run with.</summary>
    private class FirstSystem(List<string> order, UpdateType phase = UpdateType.Update) : ISystem
    {
        public UpdateType Phase => phase;

        public List<Frame> Frames { get; } = [];

        public void Run(in Frame frame)
        {
            order.Add(GetType().Name);
            Frames.Add(frame);
        }
    }

    private sealed class SecondSystem(List<string> order) : FirstSystem(order);
}
