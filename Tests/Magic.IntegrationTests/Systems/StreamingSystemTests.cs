using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Services;
using Magic.Systems.Streaming;
using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>A component declared outside Core: a chunk names it and it loads with no registration.</summary>
public struct Spin : IComponent
{
    public float Speed { get; set; }
}

/// <summary>
/// The streaming system over chunk files in a temp folder, on the real Flecs gem. The world is a row of chunks along
/// +Z (0_0_0 to 0_0_6) and one behind the origin (0_0_-2); a camera spawned at the origin looks down the row.
/// </summary>
public sealed class StreamingSystemTests : IDisposable
{
    private static readonly Handle<World> Test = new(3001);
    private static readonly Handle<World> Other = new(3050);

    private readonly TempFolder _root = new();
    private readonly Events _events = new();
    private readonly FlecsEcs _ecs = new();
    private readonly Services.Assets _assets;
    private readonly TransformSystem _transforms;
    private readonly StreamingSystem _streaming;

    public StreamingSystemTests()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources") };
        Write("Test/Test.world", """{ "chunkSize": 64 }""", 3001);
        Write("Test/globals.chunk", """
            { "entities": [
                { "id": 7, "name": "Sun", "tags": [ "static" ],
                  "components": { "Transform": { "position": { "x": 1, "y": 2, "z": 3 } } },
                  "children": [ { "name": "Child", "components": { "Spin": { "speed": 2 }, "Nope": { "a": 1 } } } ] }
            ] }
            """, 3003);
        Write("Test/0_0_-2.chunk", """{ "entities": [ { "name": "Behind", "components": { "Transform": {} } } ] }""", 3012);

        // Model 600 is not an asset here: the two chunks naming it share one (failed) load.
        Write("Test/0_0_0.chunk", """{ "entities": [ { "name": "A", "components": { "Transform": {}, "Renderer": { "model": { "id": 600 } } } } ] }""", 3020);
        Write("Test/0_0_1.chunk", """{ "entities": [ { "name": "B", "components": { "Transform": {}, "Renderer": { "model": { "id": 600 } } } } ] }""", 3021);
        for (int z = 2; z <= 6; z++)
            Write($"Test/0_0_{z}.chunk", """{ "entities": [ { "name": "Far", "components": { "Transform": {} } } ] }""", 3020ul + (ulong)z);

        Write("Other/Other.world", """{ "chunkSize": 16 }""", 3050);

        _assets = new Services.Assets(project, new FileSystem(), _events, new Container());
        _transforms = new TransformSystem(_ecs);
        _streaming = new StreamingSystem(_ecs, _assets, project);

        // The watcher can still report the files just written; let those settle and drain them, or they would reload the world mid-test.
        Thread.Sleep(400);
        _assets.ProcessChanges();
        _events.NextFrame();
    }

    [Fact]
    public void Opening_a_world_spawns_its_globals()
    {
        _streaming.Open(Test);

        Assert.True(_ecs.Lookup("World.Globals.Sun").IsValid);
    }

    [Fact]
    public void A_spawned_entity_has_the_components_its_chunk_gives_it()
    {
        _streaming.Open(Test);

        Assert.Equal(new Vector3(1, 2, 3), _ecs.Get<Transform>(_ecs.Lookup("World.Globals.Sun")).Position);
    }

    [Fact]
    public void A_spawned_entity_has_the_tags_its_chunk_gives_it()
    {
        _streaming.Open(Test);

        Assert.True(_ecs.Get<Tags>(_ecs.Lookup("World.Globals.Sun")).Has(Tag.Static));
    }

    [Fact]
    public void A_spawned_entity_has_the_id_its_chunk_gives_it()
    {
        _streaming.Open(Test);

        Assert.Equal(7ul, _ecs.Get<EntityId>(_ecs.Lookup("World.Globals.Sun")).Value);
    }

    [Fact]
    public void A_child_spawns_under_its_parent()
    {
        _streaming.Open(Test);

        Assert.True(_ecs.Lookup("World.Globals.Sun.Child").IsValid);
    }

    [Fact]
    public void A_component_declared_outside_core_is_found_by_name()
    {
        _streaming.Open(Test);

        Assert.Equal(2f, _ecs.Get<Spin>(_ecs.Lookup("World.Globals.Sun.Child")).Speed);
    }

    [Fact]
    public void Opening_a_second_world_destroys_the_first()
    {
        _streaming.Open(Test);

        _streaming.Open(Other);

        Assert.False(_ecs.Lookup("World.Globals.Sun").IsValid);
    }

    [Fact]
    public void A_camera_streams_in_the_chunk_it_stands_in()
    {
        _streaming.Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Loaded.Contains((0, 0, 0)));

        Assert.True(_ecs.Lookup("World.Chunks.C0_0_0.A").IsValid);
    }

    [Fact]
    public void The_chunk_the_camera_stands_in_is_active()
    {
        _streaming.Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Loaded.Contains((0, 0, 0)));

        Assert.True(_streaming.IsActive((0, 0, 0)));
    }

    [Fact]
    public void A_camera_streams_in_every_chunk_it_sees()
    {
        _streaming.Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Loaded.Count == 7);

        Assert.Equal(Enumerable.Range(0, 7).Select(z => (0, 0, z)), _streaming.Loaded.OrderBy(c => c.Z));
    }

    [Fact]
    public void Only_a_few_loads_start_at_a_time_nearest_first()
    {
        _streaming.Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Loaded.Count > 0);

        Assert.All(_streaming.Loaded, c => Assert.InRange(c.Z, 0, StreamingSystem.MaxLoads - 1));
    }

    [Fact]
    public void A_chunk_no_longer_seen_is_kept_for_the_unload_delay()
    {
        _streaming.Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => _streaming.Loaded.Contains((0, 0, 2)));
        TurnAround(camera);

        Step(StreamingSystem.UnloadDelayFrames / 2);

        Assert.Contains((0, 0, 2), _streaming.Loaded);
    }

    [Fact]
    public void A_chunk_no_longer_seen_is_unloaded_after_the_unload_delay()
    {
        _streaming.Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => _streaming.Loaded.Contains((0, 0, 2)));
        TurnAround(camera);

        Step(StreamingSystem.UnloadDelayFrames + 5);

        Assert.DoesNotContain((0, 0, 2), _streaming.Loaded);
    }

    [Fact]
    public void A_chunk_within_a_chunk_of_the_camera_stays_wherever_it_looks()
    {
        _streaming.Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => _streaming.Loaded.Contains((0, 0, 1)));
        TurnAround(camera);

        Step(StreamingSystem.UnloadDelayFrames + 5);

        Assert.Contains((0, 0, 1), _streaming.Loaded);
    }

    [Fact]
    public void A_changed_chunk_file_respawns_its_entities()
    {
        _streaming.Open(Test);
        SpawnCamera();
        StepUntil(() => _streaming.Loaded.Contains((0, 0, 0)));

        Write("Test/0_0_0.chunk", """{ "entities": [ { "name": "A2", "components": { "Transform": {} } } ] }""", 3020);
        StepUntil(() => _ecs.Lookup("World.Chunks.C0_0_0.A2").IsValid);

        Assert.True(_ecs.Lookup("World.Chunks.C0_0_0.A2").IsValid);
    }

    [Fact]
    public void Gems_changing_mid_load_still_spawns_each_chunk_once()
    {
        _streaming.Open(Test);
        SpawnCamera();
        Step(1); // the loads are in flight

        _events.Publish(new Event(EventType.GemsChanged));
        StepUntil(() => _streaming.Loaded.Count == 7);

        Assert.Equal(7, _ecs.GetChildren(_ecs.Lookup("World.Chunks")).Length);
    }

    [Fact]
    public void Chunks_naming_one_model_load_it_once()
    {
        _streaming.Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Loaded.Contains((0, 0, 0)) && _streaming.Loaded.Contains((0, 0, 1)));

        Assert.Equal(1, Assert.Single(_assets.PoolStats(), s => s.Type == nameof(Model)).Misses);
    }

    public void Dispose()
    {
        _streaming.Dispose();
        _transforms.Dispose();
        _assets.Dispose();
        _ecs.Dispose();
        _root.Dispose();
    }

    /// <summary>A camera in chunk 0_0_0 looking down +Z.</summary>
    private Handle SpawnCamera()
    {
        Handle camera = _ecs.Create("Camera");
        _ecs.Set(camera, new Transform { Position = new Vector3(5, 5, 5) });
        _ecs.Set(camera, Camera.Perspective(60f, 0.1f));

        return camera;
    }

    private void TurnAround(Handle camera)
    {
        _ecs.Get<Transform>(camera).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
    }

    /// <summary>What the frame loop does for these systems, <paramref name="frames"/> times: changes, then Update, then LateUpdate.</summary>
    private void Step(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _assets.ProcessChanges();
            Frame frame = new(1, 0, 1f / 60f, _events.NextFrame());
            _ecs.Update(frame);
            _streaming.Update(frame);
            _ecs.LateUpdate(frame);
            _transforms.Update(frame);
        }
    }

    /// <summary>Steps until <paramref name="condition"/> holds, giving the workers and the file watcher up to five seconds.</summary>
    private void StepUntil(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Step(1);
            Thread.Sleep(10);
        }
    }

    private void Write(string file, string json, ulong id)
    {
        _root.Write($"Assets/Worlds/{file}", json);
        _root.Write($"Assets/Worlds/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }
}
