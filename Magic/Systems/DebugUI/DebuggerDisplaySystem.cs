using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.Rendering;
using Magic.Systems.Streaming;
using Magic.Utils;

namespace Magic.Systems.DebugUI;

/// <summary>
/// The engine's own numbers in one place: frames per second and frame time, what the transform, render and
/// streaming systems are doing, and the renderer's counters. Drawn as the "Debug" window while open (F3), and logged
/// (verbose, so only with --verbose) every <see cref="LogIntervalMs"/> whether open or not, so a headless or scripted
/// run can have them too. Reads the other systems, changes nothing.
/// </summary>
internal sealed class DebuggerDisplaySystem : DebugWindowSystem
{
    public const double LogIntervalMs = 10000;

    private readonly TransformSystem _transforms;
    private readonly RenderSystem _rendering;
    private readonly StreamingSystem _streaming;
    private readonly Assets _assets;

    private double _frameMs;
    private double _lastFrameMs;
    private double _windowWorstMs;
    private double _sinceLogMs;

    public DebuggerDisplaySystem(TransformSystem transforms, RenderSystem rendering, StreamingSystem streaming, Assets assets, IInput? input) : base(Key.F3, input)
    {
        _transforms = transforms;
        _rendering = rendering;
        _streaming = streaming;
        _assets = assets;
    }

    protected override void Draw(in Frame frame)
    {
        double dtMs = frame.Delta * 1000d;
        _frameMs = _frameMs == 0 ? dtMs : _frameMs + ((dtMs - _frameMs) * 0.05);
        _windowWorstMs = Math.Max(_windowWorstMs, dtMs);

        double fps = _frameMs > 0 ? 1000 / _frameMs : 0;
        RenderStats render = _rendering.Stats;
        StreamingStats streaming = _streaming.Stats;

        if (Open)
        {
            Debugging.UI.Begin("Debug");
            Debugging.UI.Text($"Frame {_frameMs:F2} ms ({fps:F0} fps), worst {_lastFrameMs:F2} ms");
            Debugging.UI.Text($"Transforms {_transforms.LastMs:F2} ms, render sync {_rendering.SyncMs:F2} ms, render {_rendering.RenderMs:F2} ms");
            Debugging.UI.Text($"Instances {render.Instances}");
            Debugging.UI.Text($"Draws {render.Draws}, dispatches {render.Dispatches}, pipelines pending {render.PipelinesPending}");
            Debugging.UI.Text($"Resident meshes {render.ResidentMeshes}, textures {render.ResidentTextures}; renderer sync {render.CpuSyncMs:F2} ms, record {render.CpuRecordMs:F2} ms, GPU wait {render.CpuWaitMs:F2} ms");
            Debugging.UI.Text($"Streaming: {streaming.Domains} domain(s), {streaming.Loaded} chunk(s) loaded ({streaming.Active} active, " +
                $"{streaming.Loading} loading, {streaming.PendingUnload} unloading), {streaming.Entities} entities, {streaming.Cameras} camera(s).");
            foreach (AssetPoolStats pool in _assets.PoolStats())
                Debugging.UI.Text($"Pool {pool.Type} {pool.Count} ({Megabytes(pool.Bytes):F1} / {Megabytes(pool.Budget):F0} MB), hits {pool.Hits}, misses {pool.Misses}");
            Debugging.UI.End();
        }

        _sinceLogMs += dtMs;
        if (_sinceLogMs >= LogIntervalMs)
        {
            _sinceLogMs = 0;
            Debugging.Log.Verbose($"Frame {_frameMs:F2} ms ({fps:F0} fps, worst {_lastFrameMs:F2}): transforms {_transforms.LastMs:F2}, render sync {_rendering.SyncMs:F2}, " +
                $"render {_rendering.RenderMs:F2} (renderer sync {render.CpuSyncMs:F2}, record {render.CpuRecordMs:F2}, GPU wait {render.CpuWaitMs:F2}) ms; " +
                $"{render.Instances} instances, {render.Draws} draws, {render.Dispatches} dispatches.");
        }

        if (fps < 30)
            Debugging.Log.Verbose("FPS is below 30! Consider profiling and optimizing.");

        _lastFrameMs = _windowWorstMs;
        _windowWorstMs = 0;
    }

    private static double Megabytes(long bytes)
    {
        return bytes / (1024d * 1024d);
    }
}
