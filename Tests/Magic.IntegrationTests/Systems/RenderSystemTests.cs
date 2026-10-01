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
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>The render system driven through the real Flecs gem and the repo's shaders, with a fake renderer.</summary>
public sealed class RenderSystemTests : IDisposable
{
    private static readonly Renderer Cube = new() { Model = new Handle<Model>(514) };

    private readonly TempFolder _root = new();
    private readonly FlecsEcs _ecs = new();
    private readonly FakeRendering _gpu = new();
    private readonly FakeWindows _windows = new();
    private readonly RenderCommands _commands = new();
    private readonly Services.Assets _assets;
    private readonly TransformSystem _transforms;
    private readonly RenderSystem _rendering;

    public RenderSystemTests()
    {
        // The repo root is stamped into Magic.dll: the render system compiles (fakes) the engine's own shaders.
        string repo = typeof(Project).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MagicRoot").Value!;
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
        _rendering = new RenderSystem(_ecs, _assets, files, project);
    }

    [Fact]
    public void A_renderer_is_registered_as_an_instance()
    {
        Handle mover = Spawn(Vector3.Zero);
        _ecs.Set(mover, Cube);

        Frame(_gpu);

        Assert.Equal(1u, _rendering.Context!.Instances.Alive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_instance_is_static_when_its_entity_is(bool isStatic)
    {
        Handle e = Spawn(Vector3.Zero, isStatic);
        _ecs.Set(e, Cube);

        Frame(_gpu);

        Assert.Equal(isStatic, _ecs.Get<RenderInstance>(e).Static);
    }

    [Fact]
    public void A_camera_frame_culls_on_the_gpu()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        Frame(_gpu);

        Assert.Contains(RenderCommandType.Dispatch, Assert.Single(_gpu.Submitted));
    }

    [Fact]
    public void A_camera_frame_ends_by_presenting_into_its_window()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        Frame(_gpu);

        Assert.Equal(RenderCommandType.Blit, Assert.Single(_gpu.Submitted)[^1]);
    }

    [Fact]
    public void A_main_window_no_camera_draws_into_is_cleared()
    {
        Frame(_gpu);

        Assert.Equal([RenderCommandType.BeginRenderPass, RenderCommandType.EndRenderPass], Assert.Single(_gpu.Submitted));
    }

    [Fact]
    public void Commands_added_after_the_scene_is_recorded_are_submitted_after_it()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));
        _transforms.Update(default);
        _rendering.Record(new Frame(1, 0, 0.016f, default, _commands), _gpu, _windows);

        _commands.Draw(3); // what a gem's Render hook adds
        _gpu.Submit(_commands);

        Assert.Equal(RenderCommandType.Draw, Assert.Single(_gpu.Submitted)[^1]);
    }

    [Fact]
    public void A_moved_instance_takes_its_new_world_matrix()
    {
        Handle mover = Spawn(new Vector3(2, 0, 0));
        _ecs.Set(mover, Cube);
        Frame(_gpu);
        _ecs.Get<Transform>(mover).Position = new Vector3(5, 0, 0);

        Frame(_gpu);

        Assert.Equal(5f, XformOf(mover).R0.W);
    }

    [Fact]
    public void A_static_instance_keeps_its_first_world_matrix()
    {
        Handle rock = Spawn(new Vector3(1, 0, 0), isStatic: true);
        _ecs.Set(rock, Cube);
        Frame(_gpu);
        _ecs.Get<Transform>(rock).Position = new Vector3(9, 0, 0);

        Frame(_gpu);

        Assert.Equal(1f, XformOf(rock).R0.W);
    }

    [Fact]
    public void Setting_the_renderer_again_re_registers_the_instance_with_its_new_model()
    {
        Handle e = Spawn(Vector3.Zero);
        _ecs.Set(e, Cube);
        Frame(_gpu);
        _ecs.Set(e, new Renderer { Model = new Handle<Model>(515) });

        Frame(_gpu);

        Assert.Equal(515ul, _rendering.Context!.Instances.ModelOf(_ecs.Get<RenderInstance>(e).Handle));
    }

    [Fact]
    public void Setting_the_renderer_again_leaves_no_instance_behind()
    {
        Handle e = Spawn(Vector3.Zero);
        _ecs.Set(e, Cube);
        Frame(_gpu);
        _ecs.Set(e, new Renderer { Model = new Handle<Model>(515) });

        Frame(_gpu);

        Assert.Equal(1u, _rendering.Context!.Instances.Alive);
    }

    [Fact]
    public void Destroying_an_entity_unregisters_its_instance()
    {
        Handle e = Spawn(Vector3.Zero);
        _ecs.Set(e, Cube);
        Frame(_gpu);
        _ecs.Destroy(e);

        Frame(_gpu);

        Assert.Equal(0u, _rendering.Context!.Instances.Alive);
    }

    [Fact]
    public void A_new_renderer_gets_every_instance_again()
    {
        Handle e = Spawn(Vector3.Zero);
        _ecs.Set(e, Cube);
        Frame(_gpu);

        Frame(new FakeRendering());

        Assert.Equal(1u, _rendering.Context!.Instances.Alive);
    }

    [Fact]
    public void Losing_the_renderer_drops_every_instance()
    {
        Handle e = Spawn(Vector3.Zero);
        _ecs.Set(e, Cube);
        Frame(_gpu);

        Frame(null);

        Assert.False(_ecs.Has<RenderInstance>(e));
    }

    public void Dispose()
    {
        _rendering.Dispose();
        _transforms.Dispose();
        _ecs.Dispose();
        _assets.Dispose();
        _root.Dispose();
    }

    /// <summary>What the frame loop does for these two systems after LateUpdate: transforms, then record, submit and finish.</summary>
    private void Frame(IRendering? rendering)
    {
        Frame frame = new(1, 0, 0.016f, default, _commands);
        _transforms.Update(frame);
        _rendering.Record(frame, rendering, _windows);
        float waitMs = 0f;
        if (rendering is not null)
            waitMs = rendering.Submit(_commands);
        else
            _commands.Clear();

        _rendering.Finish(frame, 0f, waitMs);
    }

    private Handle Spawn(Vector3 position, bool isStatic = false)
    {
        Handle e = _ecs.Create();
        _ecs.Set(e, new Transform { Position = position });
        if (isStatic)
            _ecs.Set(e, Tags.Of(Tag.Static));

        return e;
    }

    private GpuInstanceXform XformOf(Handle entity)
    {
        return _rendering.Context!.Instances.Xforms[(int)_ecs.Get<RenderInstance>(entity).Handle];
    }
}
