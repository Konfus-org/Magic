using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using Magic.Contexts.Frames;
using Magic.Contexts.Threading;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// The frames, on two threads. The main thread runs a frame's Update, FixedUpdate and LateUpdate, then hands it to the
/// render thread for Render and goes on to the next frame; before the next LateUpdate it waits until the render thread
/// is done, so the render thread runs at most one frame behind and LateUpdate is a point where both hold still: there,
/// first, the settings changed since the last frame are copied into the gems' objects (<see cref="Settings"/>). The
/// main thread's phases run each gem's hook in the order the gems were made, Render in reverse, and only on the gems
/// that implement it. FixedUpdate runs once for every <see cref="FixedStep"/> since the last frame. The frames are
/// measured on the meter <c>Magic</c>.
/// </summary>
internal sealed unsafe class FrameLoop : IDisposable
{
    /// <summary>
    /// The time FixedUpdate moves the world on by: 60 steps a second.
    /// </summary>
    private const double FixedStep = 1.0 / 60;

    /// <summary>
    /// The most fixed steps one frame runs. A frame further behind drops the rest, so a slow frame does not make the
    /// next slower still.
    /// </summary>
    private const int MaxFixedSteps = 8;

    /// <summary>
    /// The bucket boundaries, in seconds, advised to exporters that count a histogram in buckets: fine below a
    /// millisecond, where most gem hooks fall, with edges at the frame budgets of 240, 144, 120, 60 and 30 frames a
    /// second. Their own are made for milliseconds, and would put every frame in the first.
    /// </summary>
    private static readonly InstrumentAdvice<double> _secondsBuckets = new()
    {
        HistogramBucketBoundaries = [0.0001, 0.00025, 0.0005, 0.001, 0.002, 1.0 / 240, 1.0 / 144, 1.0 / 120, 1.0 / 60, 1.0 / 30, 0.05, 0.1, 0.25, 0.5, 1],
    };

    private static readonly Meter _meter = new("Magic");
    private static readonly Histogram<double> _frameDuration = _meter.CreateHistogram("magic.frame.duration", "s", "Time from one frame's start to the next's.", tags: null, advice: _secondsBuckets);
    private static readonly Histogram<double> _renderWait = _meter.CreateHistogram("magic.frame.render_wait.duration", "s", "How long the main thread waited for the render thread to finish the frame before, ahead of LateUpdate.", tags: null, advice: _secondsBuckets);
    private static readonly Histogram<double> _hookDuration = _meter.CreateHistogram("magic.gem.hook.duration", "s", "How long one gem's hook took, tagged with the gem's name and the phase.", tags: null, advice: _secondsBuckets);
    private static readonly string[] _phases = Enum.GetNames<Phase>();

    private readonly IGem[] _gems;

    /// <summary>
    /// The gems' names as the project lists them, which their assemblies are named for.
    /// </summary>
    private readonly string[] _names;

    /// <summary>
    /// For each phase, the gems that implement its hook, by index, in the order the phase runs them. A hook a gem leaves
    /// to <see cref="IGem"/>'s own does nothing, so it is never called, nor timed.
    /// </summary>
    private readonly int[][] _hooked;

    /// <summary>
    /// The gems that threw and left the frame loop, by index: written and read on both threads.
    /// </summary>
    private readonly bool[] _left;

    private readonly Lifetime _lifetime;
    private readonly Threads _threads;
    private readonly Events _events;
    private readonly Settings _settings;
    private readonly ulong _frameLimit;

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

    public FrameLoop(IReadOnlyList<IGem> gems, Lifetime lifetime, Threads threads, Events events, Settings settings, ulong frameLimit)
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
            _hooked[phase] = Hooked((Phase)phase);
        }

        _left = new bool[_gems.Length];
        _lifetime = lifetime;
        _threads = threads;
        _events = events;
        _settings = settings;
        _frameLimit = frameLimit;
        _renderFrame = RenderFrame;
    }

    public void Dispose()
    {
        _rendered.Dispose();
    }

    /// <summary>
    /// Runs frames until the run stops or the frame limit is reached, then waits for the last to be rendered. On the main
    /// thread.
    /// </summary>
    public void Run()
    {
        long start = Stopwatch.GetTimestamp();
        double previousSeconds = 0;
        for (ulong index = 0; !_lifetime.IsStopping; index++)
        {
            if (index == _frameLimit)
            {
                _lifetime.Stop($"ran {_frameLimit} frames");
                break;
            }

            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Frame frame = new(index, (float)(seconds - previousSeconds), seconds);
            if (index > 0)
            {
                _frameDuration.Record(seconds - previousSeconds);
            }

            previousSeconds = seconds;
            Debugging.Frame = index;

            _threads.RunMainWork();
            _events.Deliver();
            RunPhase(Phase.Update, &Update, frame);
            RunFixedSteps(frame);
            WaitForRender();
            _settings.ApplyChanges();
            RunPhase(Phase.LateUpdate, &LateUpdate, frame);

            _rendering = frame;
            _rendered.Reset();
            _threads.Post(EngineThread.Render, _renderFrame);
        }

        WaitForRender();
    }

    private static void Update(IGem gem, in Frame frame)
    {
        gem.Update(frame);
    }

    private static void FixedUpdate(IGem gem, in Frame frame)
    {
        gem.FixedUpdate(frame);
    }

    private static void LateUpdate(IGem gem, in Frame frame)
    {
        gem.LateUpdate(frame);
    }

    private static void Render(IGem gem, in Frame frame)
    {
        gem.Render(frame);
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
            RunPhase(Phase.FixedUpdate, &FixedUpdate, frame with { DeltaSeconds = (float)FixedStep, Seconds = _fixedSeconds });
        }
    }

    /// <summary>
    /// The frame handed over, on the render thread.
    /// </summary>
    private void RenderFrame()
    {
        try
        {
            RunPhase(Phase.Render, &Render, _rendering);
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
        if (timed)
        {
            _renderWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
        }
    }

    /// <summary>
    /// Calls <paramref name="hook"/>, the hook for <paramref name="phase"/>, on every gem that implements it and is still
    /// in the loop, timing each while something listens. A gem that throws is logged and leaves the frame loop for good,
    /// on both threads; the frame goes on without it.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the engine goes on.")]
    private void RunPhase(Phase phase, delegate*<IGem, in Frame, void> hook, in Frame frame)
    {
        foreach (int i in _hooked[(int)phase])
        {
            if (Volatile.Read(ref _left[i]))
            {
                continue;
            }

            bool timed = _hookDuration.Enabled;
            long start = timed ? Stopwatch.GetTimestamp() : 0;
            try
            {
                hook(_gems[i], frame);
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
                _hookDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, new("magic.gem.name", _names[i]), new("magic.frame.phase", _phases[(int)phase]));
            }
        }
    }

    /// <summary>
    /// The gems that implement <paramref name="phase"/>'s hook, which is named for it, by index, in the order the phase
    /// runs them.
    /// </summary>
    private int[] Hooked(Phase phase)
    {
        List<int> hooked = [];
        for (int turn = 0; turn < _gems.Length; turn++)
        {
            // Render from the last gem made to the first, so a gem finishes what the gems that take it drew.
            int i = phase == Phase.Render ? _gems.Length - 1 - turn : turn;
            InterfaceMapping map = _gems[i].GetType().GetInterfaceMap(typeof(IGem));
            for (int method = 0; method < map.InterfaceMethods.Length; method++)
            {
                if (string.Equals(map.InterfaceMethods[method].Name, _phases[(int)phase], StringComparison.Ordinal) && map.TargetMethods[method].DeclaringType != typeof(IGem))
                {
                    hooked.Add(i);
                }
            }
        }

        return [.. hooked];
    }
}
