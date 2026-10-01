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

/// <summary>
/// The streaming system over chunk files in a temp folder, on the real Flecs gem. The Test domain is a row of chunks
/// along +Z (0_0_0 to 0_0_6) and one behind the origin (0_0_-2); a camera spawned at the origin looks down the row.
/// Its globals hold a Sun. The Other domain has smaller cubes and one chunk at the origin. Domains open and close by
/// the events the world publishes.
/// </summary>
public sealed class StreamingSystemTests : IDisposable
{
    private static readonly Handle<Domain> Test = new(3001);
    private static readonly Handle<Domain> Other = new(3050);

    private readonly TempFolder _root = new();
    private readonly Events _events = new();
    private readonly FlecsEcs _ecs = new();
    private readonly Services.Assets _assets;
    private readonly TransformSystem _transforms;
    private readonly ScriptSystem _scripts;
    private readonly StreamingSystem _streaming;

    public StreamingSystemTests()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources") };
        Write("Test/Test.domain", """{ "chunkSize": 64 }""", 3001);
        Write("Test/globals.chunk", """
            { "entities": [
                { "id": 7, "name": "Sun", "tags": [ "static" ],
                  "components": { "Transform": { "position": { "x": 1, "y": 2, "z": 3 } } },
                  "children": [ { "name": "Child", "components": { "Spin": { "speed": 2 }, "Nope": { "a": 1 } }, "scripts": [ { "id": 9 } ] } ] }
            ] }
            """, 3003);
        Write("Test/0_0_-2.chunk", """{ "entities": [ { "name": "Behind", "components": { "Transform": {} } } ] }""", 3012);

        // Model 600 is not an asset here: the two chunks naming it share one (failed) load.
        Write("Test/0_0_0.chunk", """{ "entities": [ { "name": "A", "components": { "Transform": {}, "Renderer": { "model": { "id": 600 } } } } ] }""", 3020);
        Write("Test/0_0_1.chunk", """{ "entities": [ { "name": "B", "components": { "Transform": {}, "Renderer": { "model": { "id": 600 } } } } ] }""", 3021);
        for (int z = 2; z <= 6; z++)
            Write($"Test/0_0_{z}.chunk", """{ "entities": [ { "name": "Far", "components": { "Transform": {} } } ] }""", 3020ul + (ulong)z);

        Write("Other/Other.domain", """{ "chunkSize": 16 }""", 3050);
        Write("Other/0_0_0.chunk", """{ "entities": [ { "name": "O", "components": { "Transform": {} } } ] }""", 3051);

        Container container = new();
        _assets = new Services.Assets(project, new FileSystem(), _events, container);
        _transforms = new TransformSystem(_ecs);
        _scripts = new ScriptSystem(_ecs, _assets, new Scheduler(), container);
        _streaming = new StreamingSystem(_ecs, _assets, project, _scripts);

        // The watcher can still report the files just written; let those settle and drain them, or they would reopen the domain mid-test.
        Thread.Sleep(400);
        _assets.ProcessChanges();
        _events.NextFrame();
    }

    public void Dispose()
    {
        _scripts.Dispose();
        _streaming.Dispose();
        _transforms.Dispose();
        _assets.Dispose();
        _ecs.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void An_opened_domain_spawns_its_globals()
    {
        Open(Test);

        Assert.True(_ecs.Lookup("World.Test.Globals.Sun").IsValid);
    }

    [Fact]
    public void A_spawned_entity_has_the_components_its_chunk_gives_it()
    {
        Open(Test);

        Assert.Equal(new Vector3(1, 2, 3), _ecs.Get<Transform>(_ecs.Lookup("World.Test.Globals.Sun")).Position);
    }

    [Fact]
    public void A_spawned_entity_has_the_tags_its_chunk_gives_it()
    {
        Open(Test);

        Assert.True(_ecs.Get<Tags>(_ecs.Lookup("World.Test.Globals.Sun")).Has(Tag.Static));
    }

    [Fact]
    public void A_spawned_entity_has_the_id_its_chunk_gives_it()
    {
        Open(Test);

        Assert.Equal(7ul, _ecs.Get<EntityId>(_ecs.Lookup("World.Test.Globals.Sun")).Value);
    }

    [Fact]
    public void A_child_spawns_under_its_parent()
    {
        Open(Test);

        Assert.True(_ecs.Lookup("World.Test.Globals.Sun.Child").IsValid);
    }

    [Fact]
    public void A_component_declared_outside_core_is_found_by_name()
    {
        Open(Test);

        Assert.Equal(2f, _ecs.Get<Spin>(_ecs.Lookup("World.Test.Globals.Sun.Child")).Speed);
    }

    [Fact]
    public void An_entity_with_scripts_in_its_chunk_is_handed_to_the_script_system()
    {
        Open(Test);

        Assert.True(_ecs.Has<Scripts>(_ecs.Lookup("World.Test.Globals.Sun.Child")));
    }

    [Fact]
    public void A_closed_domain_loses_its_entities()
    {
        Open(Test);

        Close(Test);

        Assert.False(_ecs.Lookup("World.Test.Globals.Sun").IsValid);
    }

    [Fact]
    public void A_domain_that_is_not_an_asset_spawns_nothing()
    {
        Open(new Handle<Domain>(9999));

        Assert.Equal(0, _streaming.Stats.Domains);
    }

    [Fact]
    public void A_changed_domain_file_reopens_the_domain()
    {
        Open(Test);
        Handle before = _ecs.Lookup("World.Test");

        Write("Test/Test.domain", """{ "chunkSize": 32 }""", 3001);
        StepUntil(() => _ecs.Lookup("World.Test") is { IsValid: true } root && root != before);

        Assert.NotEqual(before, _ecs.Lookup("World.Test"));
    }

    [Fact]
    public void A_changed_global_chunk_respawns_the_globals()
    {
        Open(Test);

        Write("Test/globals.chunk", """{ "entities": [ { "name": "Star" } ] }""", 3003);
        StepUntil(() => _ecs.Lookup("World.Test.Globals.Star").IsValid);

        Assert.True(_ecs.Lookup("World.Test.Globals.Star").IsValid);
    }

    [Fact]
    public void A_camera_streams_in_the_chunk_it_stands_in()
    {
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 0));

        Assert.True(_ecs.Lookup("World.Test.Chunks.C0_0_0.A").IsValid);
    }

    [Fact]
    public void The_chunk_the_camera_stands_in_is_active()
    {
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 0));

        Assert.Equal(1, _streaming.Stats.Active);
    }

    [Fact]
    public void A_camera_at_a_negative_position_streams_in_the_chunk_it_stands_in()
    {
        Open(Test);
        Handle camera = SpawnCamera();
        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, -70);

        StepUntil(() => Spawned("Test", 0, 0, -2));

        Assert.True(Spawned("Test", 0, 0, -2));
    }

    [Fact]
    public void A_camera_streams_in_every_chunk_it_sees()
    {
        Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Stats.Loaded == 7);

        Assert.Equal(Enumerable.Range(0, 7).Select(z => $"C0_0_{z}"), _ecs.GetChildren(_ecs.Lookup("World.Test.Chunks")).Select(child => _ecs.GetName(child)!).Order());
    }

    [Fact]
    public void The_farthest_chunk_is_not_among_the_first_to_load()
    {
        Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Stats.Loaded > 0);

        Assert.False(Spawned("Test", 0, 0, 6));
    }

    [Fact]
    public void A_chunk_no_longer_seen_is_not_unloaded_at_once()
    {
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        Step(2);

        Assert.True(Spawned("Test", 0, 0, 2));
    }

    [Fact]
    public void A_chunk_no_longer_seen_is_unloaded_in_the_end()
    {
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        StepUntil(() => !Spawned("Test", 0, 0, 2));

        Assert.False(Spawned("Test", 0, 0, 2));
    }

    [Fact]
    public void A_chunk_within_a_chunk_of_the_camera_stays_wherever_it_looks()
    {
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 1) && Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        StepUntil(() => !Spawned("Test", 0, 0, 2)); // the far one has gone: the unload delay is over

        Assert.True(Spawned("Test", 0, 0, 1));
    }

    [Fact]
    public void A_changed_chunk_file_respawns_its_entities()
    {
        Open(Test);
        SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 0));

        Write("Test/0_0_0.chunk", """{ "entities": [ { "name": "A2", "components": { "Transform": {} } } ] }""", 3020);
        StepUntil(() => _ecs.Lookup("World.Test.Chunks.C0_0_0.A2").IsValid);

        Assert.True(_ecs.Lookup("World.Test.Chunks.C0_0_0.A2").IsValid);
    }

    [Fact]
    public void Gems_changing_mid_load_still_spawns_each_chunk_once()
    {
        Open(Test);
        SpawnCamera();
        Step(1); // the loads are in flight

        _events.Publish(new Event(EventType.GemsChanged));
        StepUntil(() => _streaming.Stats.Loaded == 7);

        Assert.Equal(7, _ecs.GetChildren(_ecs.Lookup("World.Test.Chunks")).Length);
    }

    [Fact]
    public void Chunks_naming_one_model_load_it_once()
    {
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 0) && Spawned("Test", 0, 0, 1));

        Assert.Equal(1, Assert.Single(_assets.PoolStats(), pool => pool.Type == nameof(Model)).Misses);
    }

    [Fact]
    public void Every_open_domain_streams_its_own_chunks()
    {
        Open(Test);
        Open(Other);
        SpawnCamera();

        StepUntil(() => Spawned("Other", 0, 0, 0));

        Assert.True(_ecs.Lookup("World.Other.Chunks.C0_0_0.O").IsValid);
    }

    [Fact]
    public void A_closed_domain_is_no_longer_streamed()
    {
        Open(Test);
        SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 0));

        Close(Test);

        Assert.Equal(0, _streaming.Stats.Loaded);
    }

    /// <summary>What the world publishes when a domain opens, and the frame that hears it.</summary>
    private void Open(Handle<Domain> domain)
    {
        _events.Publish(new Event(EventType.DomainOpened, Id: domain.Id));
        Step(1);
    }

    private void Close(Handle<Domain> domain)
    {
        _events.Publish(new Event(EventType.DomainClosed, Id: domain.Id));
        Step(1);
    }

    /// <summary>Whether a domain's chunk is in the ECS now, where the system spawns it: <c>World.&lt;Domain&gt;.Chunks.Cx_y_z</c>.</summary>
    private bool Spawned(string domain, int x, int y, int z)
    {
        return _ecs.Lookup($"World.{domain}.Chunks.C{x}_{y}_{z}").IsValid;
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
            _streaming.Run(frame);
            _ecs.LateUpdate(frame);
            _transforms.Run(frame);
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
        _root.Write($"Assets/Domains/{file}", json);
        _root.Write($"Assets/Domains/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }
}
