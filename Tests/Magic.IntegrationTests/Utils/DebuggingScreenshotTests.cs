using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Services;
using StreamingGem;
using Magic.UnitTests.Fakes;
using Magic.Utils;
using System.Diagnostics;
using System.Numerics;
using Xunit;
using Magic.Contexts.Domain;

namespace Magic.IntegrationTests.Utils;

/// <summary>
/// Screenshots written to a temp folder and restored from it, over the real streaming system and Flecs gem. The
/// Test domain's globals hold a camera at (1, 2, 3); the fake renderer shows one red pixel in every window.
/// </summary>
[Collection(StreamingCollection.Name)]
public sealed class DebuggingScreenshotTests : IDisposable
{
    private const string CameraPath = "World.Test.Globals.Camera";

    private static readonly Handle<Domain> Test = new(3001);

    private readonly TempFolder _root = new();
    private readonly FileSystem _files = new();
    private readonly FakeRendering _rendering = new() { Shown = new CapturedFrame(1, 1, [255, 0, 0, 255]) };
    private readonly FakeWindows _window = new();
    private readonly Events _events = new();
    private readonly World _world;
    private readonly FlecsEcs _ecs = new();
    private readonly Services.Assets _assets;
    private readonly StreamingSystem _streaming;
    private readonly string _shot;

    public DebuggingScreenshotTests()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources"), Cache = Path.Combine(_root.Path, "Cache") };
        Write("Test/Test.domain", """{ "chunkSize": 64 }""", 3001);
        Write("Test/globals.chunk", """
            { "entities": [
                { "name": "Camera", "components": { "Transform": { "position": { "x": 1, "y": 2, "z": 3 } }, "Camera": {} } }
            ] }
            """, 3003);

        Container container = new();
        _assets = new Services.Assets(project, _files, _events, container, new Threads());
        _world = new World(_events, _assets, new Threads());
        _streaming = new StreamingSystem(_ecs, _assets, LoadedTypes.Of<IComponent>(), new StreamingSettings(), new LodSettings(), [], _world, new Threads(), _rendering);
        _shot = Path.Combine(_root.Path, "shot.png");

        _world.Open(Test);
        StepUntil(() => _world.StateOf(Test) == DomainState.Loaded);
    }

    public void Dispose()
    {
        _streaming.Dispose();
        _assets.Dispose();
        _ecs.Dispose();
        _window.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_capture_writes_a_png()
    {
        Debugging.Screenshot.Capture(_files, _rendering, _window, _world, _ecs, _shot);

        Assert.True(_files.FileExists(_shot));
    }

    [Fact]
    public void A_capture_of_a_window_that_showed_nothing_fails()
    {
        _rendering.Shown = null;

        Result taken = Debugging.Screenshot.Capture(_files, _rendering, _window, _world, _ecs, _shot);

        Assert.True(taken.Failed);
    }

    [Fact]
    public void A_restore_puts_a_camera_back_where_it_was()
    {
        MoveCamera(new Vector3(40, 50, 60));
        Debugging.Screenshot.Capture(_files, _rendering, _window, _world, _ecs, _shot);
        MoveCamera(Vector3.Zero);

        Debugging.Screenshot.Restore(_files, _world, _events, _ecs, _shot);

        Assert.Equal(new Vector3(40, 50, 60), _ecs.Get<Transform>(_ecs.Lookup(CameraPath)).Position);
    }

    [Fact]
    public void A_restore_opens_the_domain_that_was_open()
    {
        Debugging.Screenshot.Capture(_files, _rendering, _window, _world, _ecs, _shot);
        _world.Close(Test);
        Step();

        Debugging.Screenshot.Restore(_files, _world, _events, _ecs, _shot);

        Assert.NotEqual(DomainState.Closed, _world.StateOf(Test));
    }

    [Fact]
    public void A_restore_places_the_cameras_of_a_domain_it_opened_once_that_is_loaded()
    {
        MoveCamera(new Vector3(40, 50, 60));
        Debugging.Screenshot.Capture(_files, _rendering, _window, _world, _ecs, _shot);
        _world.Close(Test);
        Step();

        Debugging.Screenshot.Restore(_files, _world, _events, _ecs, _shot);
        StepUntil(() => _ecs.Lookup(CameraPath).IsValid && _ecs.Get<Transform>(_ecs.Lookup(CameraPath)).Position.X == 40);

        Assert.Equal(new Vector3(40, 50, 60), _ecs.Get<Transform>(_ecs.Lookup(CameraPath)).Position);
    }

    [Fact]
    public void A_restore_from_a_png_without_state_fails()
    {
        _files.WriteBinary(_shot, new byte[] { 255, 0, 0, 255 }.Png(1, 1));

        Result restored = Debugging.Screenshot.Restore(_files, _world, _events, _ecs, _shot);

        Assert.True(restored.Failed);
    }

    [Fact]
    public void A_restore_from_a_missing_file_fails()
    {
        Result restored = Debugging.Screenshot.Restore(_files, _world, _events, _ecs, _shot);

        Assert.True(restored.Failed);
    }

    private void MoveCamera(Vector3 position)
    {
        _ecs.Get<Transform>(_ecs.Lookup(CameraPath)).Position = position;
    }

    /// <summary>
    /// What the frame loop does for these systems: changes, events, then Update.
    /// </summary>
    private void Step()
    {
        _assets.ProcessChanges();
        Frame frame = new(1, 0, 1f / 60f, _events.NextFrame(), new RenderCommands());
        _ecs.Update(frame);
        _streaming.Run(frame);
        _ecs.LateUpdate(frame);
    }

    /// <summary>
    /// Steps until <paramref name="condition"/> holds, giving the workers up to five seconds.
    /// </summary>
    private void StepUntil(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Step();
            Thread.Sleep(10);
        }
    }

    private void Write(string file, string json, ulong id)
    {
        _root.Write($"Assets/Domains/{file}", json);
        _root.Write($"Assets/Domains/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }
}
