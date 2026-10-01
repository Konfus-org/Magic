using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.IntegrationTests.Fakes;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.Rendering;
using Magic.Systems.Streaming;
using Magic.UnitTests.Fakes;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>The render system driven through the real Flecs gem and the repo's shaders, with a fake renderer.</summary>
public sealed class RenderSystemTests : IDisposable
{
    private static readonly Renderer Cube = new() { Model = new Handle<Model>(514) };

    private readonly TempFolder _root = new();
    private readonly FlecsEcs _ecs = new();
    private readonly FakeRendering _fake = new();
    private readonly FakeWindows _windows = new();
    private readonly RenderCommands _commands = new();
    private readonly Services.Assets _assets;
    private readonly TransformSystem _transforms;
    private readonly RenderSystem _rendering;

    public RenderSystemTests()
    {
        // The repo root is stamped into Magic.dll: the render system compiles (fakes) the engine's own shaders.
        string repo = typeof(Project).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(attribute => attribute.Key == "MagicRoot").Value!;
        Project project = new()
        {
            Name = "Tests",
            Root = _root.Path,
            EngineGems = AppContext.BaseDirectory,
            Resources = Path.Combine(repo, "Resources"),
            Settings = new Settings { Render = new RenderSettings { ShaderCache = false } },
        };
        Directory.CreateDirectory(project.Assets);

        FileSystem files = new();
        _assets = new Services.Assets(project, files, new Events(), new Container());
        _transforms = new TransformSystem(_ecs);
        _rendering = new RenderSystem(_ecs, _assets, files, project, _windows);
    }

    public void Dispose()
    {
        _rendering.Dispose();
        _transforms.Dispose();
        _ecs.Dispose();
        _assets.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_renderer_is_registered_as_an_instance()
    {
        Handle mover = Spawn(Vector3.Zero);
        _ecs.Set(mover, Cube);

        RenderFrame(_fake);

        Assert.Equal(1u, _rendering.Stats.Instances);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_instance_is_static_when_its_entity_is(bool isStatic)
    {
        Handle entity = Spawn(Vector3.Zero, isStatic);
        _ecs.Set(entity, Cube);

        RenderFrame(_fake);

        Assert.Equal(isStatic, _ecs.Get<RenderInstance>(entity).Static);
    }

    [Fact]
    public void A_camera_frame_culls_on_the_gpu()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame(_fake);

        Assert.Contains(RenderCommandType.Dispatch, Assert.Single(_fake.Submitted));
    }

    [Fact]
    public void A_camera_frame_ends_by_presenting_into_its_window()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame(_fake);

        Assert.Equal(RenderCommandType.Blit, Assert.Single(_fake.Submitted)[^1]);
    }

    [Fact]
    public void A_main_window_no_camera_draws_into_is_cleared()
    {
        RenderFrame(_fake);

        Assert.Equal([RenderCommandType.BeginRenderPass, RenderCommandType.EndRenderPass], Assert.Single(_fake.Submitted));
    }

    [Fact]
    public void Commands_added_after_the_scene_is_recorded_are_submitted_after_it()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));
        _transforms.Run(default);
        _rendering.Renderer = _fake;
        _rendering.Run(new Frame(1, 0, 0.016f, default, _commands));

        _commands.Draw(3); // what a gem's Render hook adds
        _fake.Submit(_commands);

        Assert.Equal(RenderCommandType.Draw, Assert.Single(_fake.Submitted)[^1]);
    }

    [Fact]
    public void A_moved_instance_uploads_its_new_world_matrix()
    {
        Handle mover = Spawn(new Vector3(2, 0, 0));
        _ecs.Set(mover, Cube);
        RenderFrame(_fake);
        _ecs.Get<Transform>(mover).Position = new Vector3(5, 0, 0);

        RenderFrame(_fake);

        Assert.True(Uploaded(new Vector4(1, 0, 0, 5)));
    }

    [Fact]
    public void A_static_instance_that_moves_uploads_nothing_new()
    {
        Handle rock = Spawn(new Vector3(1, 0, 0), isStatic: true);
        _ecs.Set(rock, Cube);
        RenderFrame(_fake);
        _ecs.Get<Transform>(rock).Position = new Vector3(9, 0, 0);

        RenderFrame(_fake);

        Assert.False(Uploaded(new Vector4(1, 0, 0, 9)));
    }

    [Fact]
    public void Setting_the_renderer_again_leaves_no_instance_behind()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderFrame(_fake);
        _ecs.Set(entity, new Renderer { Model = new Handle<Model>(515) });

        RenderFrame(_fake);

        Assert.Equal(1u, _rendering.Stats.Instances);
    }

    [Fact]
    public void Destroying_an_entity_unregisters_its_instance()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderFrame(_fake);
        _ecs.Destroy(entity);

        RenderFrame(_fake);

        Assert.Equal(0u, _rendering.Stats.Instances);
    }

    [Fact]
    public void A_new_renderer_gets_every_instance_again()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderFrame(_fake);

        RenderFrame(new FakeRendering());

        Assert.Equal(1u, _rendering.Stats.Instances);
    }

    [Fact]
    public void Losing_the_renderer_drops_every_instance()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderFrame(_fake);

        RenderFrame(null);

        Assert.False(_ecs.Has<RenderInstance>(entity));
    }

    /// <summary>What the frame loop does for these two systems after LateUpdate: transforms, then record, submit and finish.</summary>
    private void RenderFrame(IRendering? rendering)
    {
        Frame frame = new(1, 0, 0.016f, default, _commands);
        _transforms.Run(frame);
        _rendering.Renderer = rendering; // as a gem reload would; null unloads it
        _rendering.Run(frame);
        float waitMs = 0f;
        if (rendering is not null)
            waitMs = rendering.Submit(_commands);
        else
            _commands.Clear();

        _rendering.Finish(frame, 0f, waitMs);
    }

    private Handle Spawn(Vector3 position, bool isStatic = false)
    {
        Handle entity = _ecs.Create();
        _ecs.Set(entity, new Transform { Position = position });
        if (isStatic)
            _ecs.Set(entity, Tags.Of(Tag.Static));

        return entity;
    }

    /// <summary>Whether the renderer was handed an instance transform whose first row is <paramref name="row"/>: the X axis, then the X position.</summary>
    private bool Uploaded(Vector4 row)
    {
        byte[] bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in row)).ToArray();

        return _fake.Uploaded.Exists(upload => upload.AsSpan().IndexOf(bytes) >= 0);
    }
}
