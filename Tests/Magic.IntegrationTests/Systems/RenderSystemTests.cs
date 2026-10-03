using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Services;
using Magic.Systems;
using Magic.Systems.Rendering;
using Magic.Systems.Streaming;
using Magic.UnitTests.Fakes;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The render system driven through the real Flecs gem and the repo's shaders, with a fake renderer.
/// </summary>
public sealed class RenderSystemTests : IDisposable
{
    private static readonly Renderer Cube = new() { Model = new Handle<Model>(514) };

    private readonly TempFolder _root = new();
    private readonly FlecsEcs _ecs = new();
    private readonly FakeRendering _fake = new();
    private readonly FakeWindows _windows = new();
    private readonly RenderCommands _commands = new();
    private readonly Services.Assets _assets;
    private readonly TagSystem _tags;
    private readonly TransformSystem _transforms;
    private readonly RenderSystem _rendering;

    public RenderSystemTests()
    {
        // The repo root is stamped into Magic.dll: the render system compiles (fakes) the engine's own shaders.
        string repo = typeof(Project).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(attribute => attribute.Key == "MagicRoot").Value ?? throw new InvalidOperationException("Magic.dll carries no MagicRoot.");
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
        _assets = new Services.Assets(project, files, new Events(), new Container(), new Threads());
        _tags = new TagSystem(_ecs);
        _transforms = new TransformSystem(_ecs);
        _rendering = new RenderSystem(_ecs, _assets, files, project, _windows, _fake, new Threads());
    }

    public void Dispose()
    {
        _rendering.Dispose();
        _transforms.Dispose();
        _tags.Dispose();
        _ecs.Dispose();
        _windows.Dispose();
        _assets.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_renderer_is_registered_as_an_instance()
    {
        Handle mover = Spawn(Vector3.Zero);
        _ecs.Set(mover, Cube);

        RenderUntil(() => _ecs.Has<RenderInstance>(mover));

        Assert.Equal(1u, _rendering.Stats.Instances);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_instance_is_static_when_its_entity_is(bool isStatic)
    {
        Handle entity = Spawn(Vector3.Zero, isStatic);
        _ecs.Set(entity, Cube);

        RenderUntil(() => _ecs.Has<RenderInstance>(entity));

        Assert.Equal(isStatic, _ecs.Get<RenderInstance>(entity).Static);
    }

    [Fact]
    public void A_camera_frame_culls_on_the_gpu()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame();

        Assert.Contains(RenderCommandType.Dispatch, Assert.Single(_fake.Submitted));
    }

    [Fact]
    public void A_camera_frame_lights_the_scene_after_drawing_it()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame();

        RenderCommandType[] frame = Assert.Single(_fake.Submitted);
        Assert.True(Array.LastIndexOf(frame, RenderCommandType.Dispatch) > Array.LastIndexOf(frame, RenderCommandType.EndRenderPass));
    }

    [Fact]
    public void A_point_light_is_uploaded_with_its_position_and_range()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));
        _ecs.Set(Spawn(new Vector3(7, 8, 9)), new PointLight(Vector3.One, 2f, 5f));

        RenderFrame();

        Assert.True(Uploaded(new Vector4(7, 8, 9, 5)));
    }

    [Fact]
    public void A_camera_frame_ends_by_presenting_into_its_window()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame();

        Assert.Equal(RenderCommandType.Blit, Assert.Single(_fake.Submitted)[^1]);
    }

    [Fact]
    public void Without_passes_the_scene_is_copied_to_what_is_shown_before_presenting()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame();

        Assert.Equal([RenderCommandType.Blit, RenderCommandType.Blit], Assert.Single(_fake.Submitted)[^2..]);
    }

    [Fact]
    public void A_listed_pass_is_compiled()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));
        _ecs.Set(Spawn(Vector3.Zero), new PostProcessing { Passes = TonemapOnly() });

        RenderUntil(() => PassSources().Length == 1);

        Assert.Single(PassSources());
    }

    [Fact]
    public void A_pass_that_is_not_listed_is_not_compiled()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));

        RenderFrame();

        Assert.Empty(PassSources());
    }

    [Fact]
    public void A_main_window_no_camera_draws_into_is_cleared()
    {
        RenderFrame();

        Assert.Equal([RenderCommandType.BeginRenderPass, RenderCommandType.EndRenderPass], Assert.Single(_fake.Submitted));
    }

    [Fact]
    public void Commands_added_after_the_scene_is_recorded_are_submitted_after_it()
    {
        _ecs.Set(Spawn(new Vector3(0, 1, -3)), Camera.Perspective(60f, 0.1f));
        _tags.Run(default);
        _transforms.Run(default);
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
        RenderUntil(() => _ecs.Has<RenderInstance>(mover));
        _ecs.Get<Transform>(mover).Position = new Vector3(5, 0, 0);

        RenderFrame();

        Assert.True(Uploaded(new Vector4(1, 0, 0, 5)));
    }

    [Fact]
    public void A_static_instance_that_moves_uploads_nothing_new()
    {
        Handle rock = Spawn(new Vector3(1, 0, 0), isStatic: true);
        _ecs.Set(rock, Cube);
        RenderUntil(() => _ecs.Has<RenderInstance>(rock));
        _ecs.Get<Transform>(rock).Position = new Vector3(9, 0, 0);

        RenderFrame();

        Assert.False(Uploaded(new Vector4(1, 0, 0, 9)));
    }

    [Fact]
    public void Setting_the_renderer_again_leaves_no_instance_behind()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderUntil(() => _ecs.Has<RenderInstance>(entity));
        _ecs.Set(entity, new Renderer { Model = new Handle<Model>(515) });

        RenderFrame();
        RenderUntil(() => _ecs.Has<RenderInstance>(entity));

        Assert.Equal(1u, _rendering.Stats.Instances);
    }

    [Fact]
    public void Destroying_an_entity_unregisters_its_instance()
    {
        Handle entity = Spawn(Vector3.Zero);
        _ecs.Set(entity, Cube);
        RenderUntil(() => _ecs.Has<RenderInstance>(entity));
        _ecs.Destroy(entity);

        RenderFrame();

        Assert.Equal(0u, _rendering.Stats.Instances);
    }

    [Fact]
    public void An_entity_under_a_hidden_one_is_registered_hidden()
    {
        Handle parent = Spawn(Vector3.Zero);
        _ecs.Set(parent, Tags.Of(Tag.Hidden));
        Handle child = Spawn(Vector3.Zero);
        _ecs.SetParent(child, parent);
        _ecs.Set(child, Cube);

        RenderUntil(() => _ecs.Has<RenderInstance>(child));

        Assert.True(LastFlags().HasFlag(InstanceFlags.Hidden));
    }

    [Fact]
    public void Hiding_an_entity_hides_the_instances_under_it()
    {
        Handle parent = Spawn(Vector3.Zero);
        Handle child = Spawn(Vector3.Zero);
        _ecs.SetParent(child, parent);
        _ecs.Set(child, Cube);
        RenderUntil(() => _ecs.Has<RenderInstance>(child));

        _ecs.Set(parent, Tags.Of(Tag.Hidden));
        RenderFrame();

        Assert.True(LastFlags().HasFlag(InstanceFlags.Hidden));
    }

    [Fact]
    public void Showing_an_entity_shows_the_instances_under_it()
    {
        Handle parent = Spawn(Vector3.Zero);
        _ecs.Set(parent, Tags.Of(Tag.Hidden));
        Handle child = Spawn(Vector3.Zero);
        _ecs.SetParent(child, parent);
        _ecs.Set(child, Cube);
        RenderUntil(() => _ecs.Has<RenderInstance>(child));

        _ecs.Set(parent, default(Tags));
        RenderFrame();

        Assert.False(LastFlags().HasFlag(InstanceFlags.Hidden));
    }

    [Fact]
    public void A_light_under_a_hidden_entity_is_not_counted()
    {
        Handle parent = Spawn(Vector3.Zero);
        _ecs.Set(parent, Tags.Of(Tag.Hidden));
        Handle light = Spawn(Vector3.Zero);
        _ecs.SetParent(light, parent);
        _ecs.Set(light, new PointLight(Vector3.One, 2f, 5f));

        RenderFrame();

        Assert.Equal(0u, _rendering.Stats.Lights);
    }

    [Fact]
    public void A_light_is_counted_once_the_entity_above_it_is_shown()
    {
        Handle parent = Spawn(Vector3.Zero);
        _ecs.Set(parent, Tags.Of(Tag.Hidden));
        Handle light = Spawn(Vector3.Zero);
        _ecs.SetParent(light, parent);
        _ecs.Set(light, new PointLight(Vector3.One, 2f, 5f));
        RenderFrame();

        _ecs.Set(parent, default(Tags));
        RenderFrame();

        Assert.Equal(1u, _rendering.Stats.Lights);
    }

    [Theory]
    [InlineData("#define SURFACE_MASKED 0\n")]
    [InlineData("#define SURFACE_DOUBLE_SIDED 1\n")]
    [InlineData("#define FAILURE_FORCE 1\n")]
    public void A_built_in_surface_is_compiled_with_its_variant_defined(string define)
    {
        RenderFrame();

        Assert.Contains(SurfaceSources(), source => source.Contains(define));
    }

    [Fact]
    public void A_surface_is_compiled_with_its_variant_defined_before_anything_else()
    {
        RenderFrame();

        Assert.All(SurfaceSources(), source => Assert.StartsWith("#define", source));
    }

    [Theory]
    [InlineData("#line 1 \"Shaders/Surfaces/Pbr.surf.hlsl\"\n")]
    [InlineData("#line 1 \"Shaders/Templates/GBuffer.frag.hlsl\"\n")]
    public void A_surface_is_compiled_with_each_part_mapped_back_to_its_file(string directive)
    {
        RenderFrame();

        Assert.Contains(SurfaceSources(), source => source.Contains(directive));
    }

    [Fact]
    public void A_surface_is_compiled_without_its_parameter_declarations()
    {
        RenderFrame();

        Assert.DoesNotContain(SurfaceSources(), source => source.Contains("GiColor"));
    }

    /// <summary>
    /// What the frame loop does for these two systems after LateUpdate: transforms, then record and submit.
    /// </summary>
    private void RenderFrame()
    {
        Frame frame = new(1, 0, 0.016f, default, _commands);
        _tags.Run(frame);
        _transforms.Run(frame);
        _rendering.Run(frame);
        _fake.Submit(_commands);
    }

    /// <summary>
    /// Renders frames until <paramref name="condition"/> holds, giving what loads off the render thread (an entity's
    /// model and materials, a listed pass) up to five seconds to arrive.
    /// </summary>
    private void RenderUntil(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            RenderFrame();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// What the fake was handed to compile for the material pipelines: every one carries the generated loader.
    /// </summary>
    private string[] SurfaceSources()
    {
        return [.. _fake.Compiled.Where(source => source.Contains("MaterialParams LoadMaterialParams(uint slot)"))];
    }

    /// <summary>
    /// What the fake was handed to compile for passes: every one starts by naming its output's format.
    /// </summary>
    private string[] PassSources()
    {
        return [.. _fake.Compiled.Where(source => source.StartsWith("#define PASS_OUTPUT_FORMAT"))];
    }

    /// <summary>
    /// A list of the engine's tonemap pass, Resources/Passes/Tonemap.pass.
    /// </summary>
    private static PassList TonemapOnly()
    {
        PassList list = default;
        list[0] = new Handle<Pass>(10030);

        return list;
    }

    private Handle Spawn(Vector3 position, bool isStatic = false)
    {
        Handle entity = _ecs.Create();
        _ecs.Set(entity, new Transform { Position = position });
        if (isStatic)
            _ecs.Set(entity, Tags.Of(Tag.Static));

        return entity;
    }

    /// <summary>
    /// The flags of the instance row the renderer was handed last: a row is alive and, its renderer not saying
    /// otherwise, culled by the radius of its bounds.
    /// </summary>
    private InstanceFlags LastFlags()
    {
        InstanceFlags flags = InstanceFlags.None;
        foreach (byte[] upload in _fake.Uploaded)
        {
            if (upload.Length % GpuInstance.Size != 0)
                continue;

            foreach (GpuInstance row in MemoryMarshal.Cast<byte, GpuInstance>(upload))
            {
                if (row.Flags.HasFlag(InstanceFlags.Alive) && row.Sphere.W > 0f && row.CullRadius == row.Sphere.W)
                    flags = row.Flags;
            }
        }

        return flags;
    }

    /// <summary>
    /// Whether the renderer was handed an instance transform whose first row is <paramref name="row"/>: the X axis, then the X position.
    /// </summary>
    private bool Uploaded(Vector4 row)
    {
        byte[] bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in row)).ToArray();

        return _fake.Uploaded.Exists(upload => upload.AsSpan().IndexOf(bytes) >= 0);
    }
}
