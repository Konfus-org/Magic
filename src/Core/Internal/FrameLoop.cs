using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using Magic.Contexts.Frames;
using Magic.Contexts.Telemetry;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// The frames, on two threads. The main thread runs a frame's Update, FixedUpdate and LateUpdate, waits until the
/// render thread is done with the frame before, runs Sync, then hands the frame to the render thread for Render and
/// goes on to the next; so the render thread runs at most one frame behind, and Sync is the one point where both hold
/// still: there, first, the settings changed since the last frame are copied into the gems' objects
/// (<see cref="Settings"/>). The main thread's phases run each gem's hook in the order the gems were made, Render in
/// reverse, and only on the gems that implement it. FixedUpdate runs once for every <see cref="FixedStep"/> since the
/// last frame. Each frame starts with what workers finished for the main thread: the work handed to it, the world's
/// arrivals, then the events. The frames are measured on the meter <c>Magic.Core</c>.
/// </summary>
internal sealed class FrameLoop : IDisposable
{
    /// <summary>
    /// The time FixedUpdate moves the world on by: 60 steps a second.
    /// </summary>
    private const double FixedStep = 1.0 / 60;

    /// <summary>
    /// The most fixed steps one frame runs, to catch up after a slow one: a frame further behind drops the rest, so
    /// the world loses that time rather than the frame growing to make it up and the next falling further behind.
    /// Four is 67 ms of catch-up, enough for an ordinary hitch, while a frame that takes it all is bounded at four
    /// steps' cost. No cap can save a step that takes longer than the time it simulates: the loop then runs the cap's
    /// worth every frame and the world falls behind real time, and the step is what must be fixed.
    /// </summary>
    private const int MaxFixedSteps = 4;

    private static readonly Histogram<double> _frameDuration = Metrics.Meter.CreateHistogram(
        Metrics.FrameDuration,
        "s",
        "Time from one frame's start to the next's.",
        tags: null,
        advice: Metrics.FrameBuckets);
    private static readonly Histogram<double> _renderWait = Metrics.Meter.CreateHistogram(
        Metrics.RenderWaitDuration,
        "s",
        "How long the main thread waited for the render thread to finish the frame before, ahead of Sync.",
        tags: null,
        advice: Metrics.FrameBuckets);
    private static readonly Histogram<double> _hookDuration = Metrics.Meter.CreateHistogram(
        Metrics.HookDuration,
        "s",
        "How long one gem's hook took, tagged with the gem's name and the phase.",
        tags: null,
        advice: Metrics.FrameBuckets);
    private static readonly string[] _phases = Enum.GetNames<Phase>();

    private readonly IGem[] _gems;

    /// <summary>
    /// The gems' names as the project lists them, which their assemblies are named for.
    /// </summary>
    private readonly string[] _names;

    /// <summary>
    /// For each phase, the gems that implement its hook, by index, in the order the phase runs them. A hook a gem
    /// leaves to <see cref="IGem"/>'s own does nothing, so it is never called, nor timed.
    /// </summary>
    private readonly int[][] _hooked;

    /// <summary>
    /// The gems that implement Stop, by index, in reverse order: every one of them, a gem that left the loop included,
    /// since what it owns of the main thread's still ends.
    /// </summary>
    private readonly int[] _stops;

    /// <summary>
    /// The gems that threw and left the frame loop, by index: written and read on both threads.
    /// </summary>
    private readonly bool[] _left;

    private readonly World _world;
    private readonly Lifetime _lifetime;
    private readonly Threads _threads;
    private readonly Events _events;
    private readonly Settings _settings;
    private readonly ulong _frameLimit;
    private readonly TimeSpan _timeLimit;

    /// <summary>
    /// Set while the render thread has no frame: it starts set, is reset as a frame is handed over and set again once
    /// its Render is done.
    /// </summary>
    private readonly ManualResetEventSlim _rendered = new(initialState: true);

    private readonly Action _renderFrame;

    /// <summary>
    /// The frame handed to the render thread, written before it is handed over.
    /// </summary>
    private Frame _rendering;

    /// <summary>
    /// The time the fixed steps have reached. Main thread.
    /// </summary>
    private double _fixedSeconds;

    internal FrameLoop(
        IReadOnlyList<IGem> gems,
        World world,
        Lifetime lifetime,
        Threads threads,
        Events events,
        Settings settings,
        ulong frameLimit,
        TimeSpan timeLimit,
        double startSeconds)
    {
        _gems = [.. gems];
        _names = new string[_gems.Length];
        for (int i = 0; i < _gems.Length; i++)
        {
            _names[i] = _gems[i].GetType().Assembly.GetName().Name ?? _gems[i].GetType().Name;
        }

        _hooked = new int[_phases.Length][];
        for (int phase = 0; phase < _phases.Length; phase++)
        {
            _hooked[phase] = GetHooked(_phases[phase], isReversed: phase == (int)Phase.Render);
        }

        _stops = GetHooked(nameof(IGem.Stop), isReversed: true);
        _left = new bool[_gems.Length];
        _world = world;
        _lifetime = lifetime;
        _threads = threads;
        _events = events;
        _settings = settings;
        _frameLimit = frameLimit;
        _timeLimit = timeLimit;
        _renderFrame = RenderFrame;
        _fixedSeconds = startSeconds;

        Seconds = startSeconds;
    }

    /// <summary>
    /// The frame clock (<see cref="Frame.Seconds"/>) as of the last frame started, from where the run before left
    /// it when this one goes on from it, else 0: what a run started again goes on from. Main thread.
    /// </summary>
    internal double Seconds { get; private set; }

    public void Dispose() => _rendered.Dispose();

    /// <summary>
    /// Runs frames until the run stops, the frame limit is reached or the frame clock reaches the time limit, then
    /// waits for the last to be rendered. On the main thread.
    /// </summary>
    internal void Run()
    {
        long start = Stopwatch.GetTimestamp();
        double began = Seconds;
        double previousSeconds = began;
        for (ulong index = 0; !_lifetime.IsStopping; index++)
        {
            if (index == _frameLimit)
            {
                _lifetime.Stop($"ran {_frameLimit} frames");

                break;
            }

            double seconds = began + Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (seconds >= _timeLimit.TotalSeconds)
            {
                _lifetime.Stop($"ran {_timeLimit.TotalSeconds} s");

                break;
            }

            Frame frame = new(index, (float)(seconds - previousSeconds), seconds);
            if (index > 0) _frameDuration.Record(seconds - previousSeconds);

            previousSeconds = seconds;
            Seconds = seconds;
            Debugging.Frame = index;

            _threads.RunMainWork();
            _world.TakeArrived();
            _events.Deliver();

            RunPhase(Phase.Update, frame);
            RunFixedSteps(frame);
            RunPhase(Phase.LateUpdate, frame);
            WaitForRender();

            _settings.ApplyChanges();
            RunPhase(Phase.Sync, frame);

            _rendering = frame;
            _rendered.Reset();
            _threads.HandFrame(_renderFrame);
        }

        WaitForRender();
    }

    /// <summary>
    /// Runs Stop on every gem that implements it, in reverse order, each timed while something listens; a gem that
    /// throws is logged, and the rest still stop. On the main thread, once the last frame is done and the world is
    /// saved.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws as it stops, it is logged and the rest still stop.")]
    internal void Stop()
    {
        foreach (int i in _stops)
        {
            bool timed = _hookDuration.Enabled;
            long start = timed ? Stopwatch.GetTimestamp() : 0;
            try
            {
                _gems[i].Stop();
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {_names[i]} threw in Stop: {ex}");

                continue;
            }

            if (timed)
            {
                _hookDuration.Record(
                    Stopwatch.GetElapsedTime(start).TotalSeconds,
                    new(Metrics.GemNameTag, _names[i]),
                    new(Metrics.FramePhaseTag, nameof(IGem.Stop)));
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="gem"/>'s hook for <paramref name="phase"/>.
    /// </summary>
    private static void Call(IGem gem, Phase phase, in Frame frame)
    {
        switch (phase)
        {
            case Phase.Update:
                gem.Update(frame);
                break;
            case Phase.FixedUpdate:
                gem.FixedUpdate(frame);
                break;
            case Phase.LateUpdate:
                gem.LateUpdate(frame);
                break;
            case Phase.Sync:
                gem.Sync(frame);
                break;
            case Phase.Render:
                gem.Render(frame);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "There is no such phase.");
        }
    }

    /// <summary>
    /// Runs FixedUpdate for every fixed step up to <paramref name="frame"/>'s time, each given the step and the time it
    /// reaches. Past <see cref="MaxFixedSteps"/>, the steps left are dropped.
    /// </summary>
    private void RunFixedSteps(in Frame frame)
    {
        for (int step = 0; _fixedSeconds + FixedStep <= frame.Seconds; step++)
        {
            if (step == MaxFixedSteps)
            {
                _fixedSeconds = frame.Seconds;

                break;
            }

            _fixedSeconds += FixedStep;
            RunPhase(Phase.FixedUpdate, frame with { DeltaSeconds = (float)FixedStep, Seconds = _fixedSeconds });
        }
    }

    /// <summary>
    /// The frame handed over, on the render thread.
    /// </summary>
    private void RenderFrame()
    {
        try
        {
            RunPhase(Phase.Render, _rendering);
        }
        finally
        {
            _rendered.Set();
        }
    }

    private void WaitForRender()
    {
        bool timed = _renderWait.Enabled;
        long start = timed ? Stopwatch.GetTimestamp() : 0;
        _rendered.Wait();
        if (timed) _renderWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    /// <summary>
    /// Calls the hook for <paramref name="phase"/> on every gem that implements it and is still in the loop, timing
    /// each while something listens. A gem that throws is logged and leaves the frame loop for good, on both threads;
    /// the frame goes on without it.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the engine goes on.")]
    private void RunPhase(Phase phase, in Frame frame)
    {
        foreach (int i in _hooked[(int)phase])
        {
            if (Volatile.Read(ref _left[i])) continue;

            bool timed = _hookDuration.Enabled;
            long start = timed ? Stopwatch.GetTimestamp() : 0;
            try
            {
                Call(_gems[i], phase, frame);
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {_names[i]} threw in {_phases[(int)phase]} and left the frame loop: {ex}");
                Volatile.Write(ref _left[i], true);

                continue;
            }

            // Outside the try: a listener that throws is its own failure, not this gem's.
            if (timed)
            {
                _hookDuration.Record(
                    Stopwatch.GetElapsedTime(start).TotalSeconds,
                    new(Metrics.GemNameTag, _names[i]),
                    new(Metrics.FramePhaseTag, _phases[(int)phase]));
            }
        }
    }

    /// <summary>
    /// The gems that implement the hook named <paramref name="name"/>, by index, in the order the gems were made, or
    /// the reverse when <paramref name="isReversed"/>: Render and Stop, so a gem finishes what the gems that take it
    /// drew, and ends after them.
    /// </summary>
    private int[] GetHooked(string name, bool isReversed)
    {
        List<int> hooked = [];
        for (int turn = 0; turn < _gems.Length; turn++)
        {
            int i = isReversed ? _gems.Length - 1 - turn : turn;
            InterfaceMapping map = _gems[i].GetType().GetInterfaceMap(typeof(IGem));
            for (int method = 0; method < map.InterfaceMethods.Length; method++)
            {
                if (string.Equals(map.InterfaceMethods[method].Name, name, StringComparison.Ordinal)
                    && map.TargetMethods[method].DeclaringType != typeof(IGem))
                {
                    hooked.Add(i);
                }
            }
        }

        return [.. hooked];
    }
}
