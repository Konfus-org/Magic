using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.Streaming;
using Magic.UnitTests.Fakes;
using Magic.Utils;
using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The streaming system over chunk files in a temp folder, on the real Flecs gem. The Test domain is a row of chunks
/// along +Z (0_0_0 to 0_0_6) and one behind the origin (0_0_-2); a camera spawned at the origin looks down the row.
/// Its globals hold a Sun. The Other domain has smaller cubes and one chunk at the origin. Domains open and close
/// through the world, as a game does it. A generator in the container gives every chunk a stand-in, one entity named
/// StandIn, for beyond 512 m: farther than any chunk of the row, so only a test that writes a chunk out there sees one.
/// The system is told there is a renderer, and there is none: nothing is registered unless a test does it. The Loading
/// domain is empty: a test makes it the world's loading domain to open another behind it. The Watched domain has a
/// camera of its own in its globals.
/// </summary>
public sealed class StreamingSystemTests : IDisposable
{
    private static readonly Handle<Domain> Test = new(3001);
    private static readonly Handle<Domain> Other = new(3050);
    private static readonly Handle<Domain> Loading = new(3060);
    private static readonly Handle<Domain> Watched = new(3070);

    // A chunk with one entity that draws.
    private const string Drawn = """{ "entities": [ { "name": "Far", "components": { "Transform": {}, "Renderer": { "model": { "id": 600 } } } } ] }""";

    private readonly TempFolder _root = new();
    private readonly Project _project;
    private readonly Events _events = new();
    private readonly World _world;
    private readonly FlecsEcs _ecs = new();
    private readonly Services.Assets _assets;
    private readonly TransformSystem _transforms;
    private readonly ScriptSystem _scripts;
    private readonly StreamingSystem _streaming;
    private readonly List<Event> _seen = []; // every event the frames stepped so far were given

    public StreamingSystemTests()
    {
        _project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources"), Cache = Path.Combine(_root.Path, "Cache") };
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

        Write("Loading/Loading.domain", """{ "chunkSize": 64 }""", 3060);

        Write("Watched/Watched.domain", """{ "chunkSize": 64 }""", 3070);
        Write("Watched/globals.chunk", """{ "entities": [ { "name": "Eye", "components": { "Transform": { "position": { "x": 5, "y": 5, "z": 5 } }, "Camera": {} } } ] }""", 3071);

        Container container = new();
        container.Add<ILODGenerator<Chunk>>(new StandIns());
        _assets = new Services.Assets(_project, new FileSystem(), _events, container, new Threads());
        _transforms = new TransformSystem(_ecs);
        _scripts = new ScriptSystem(_ecs, _assets, new Scheduler(), container);
        _world = new World(_events, _assets, new Threads());
        _streaming = new StreamingSystem(_ecs, _assets, _project, _scripts, _world, new Threads(), new FakeRendering());
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
    public void A_spawned_entity_is_hidden_when_its_chunk_says_so()
    {
        Write("Test/0_0_0.chunk", """{ "entities": [ { "name": "Ghost", "tags": [ "hidden" ], "components": { "Transform": {} } } ] }""", 3020);
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 0));

        Assert.True(_ecs.Has<Hidden>(_ecs.Lookup("World.Test.Chunks.C0_0_0.Ghost")));
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

        StepUntil(() => Enumerable.Range(0, 7).All(z => Spawned("Test", 0, 0, z)));

        Assert.All(Enumerable.Range(0, 7), z => Assert.True(Spawned("Test", 0, 0, z)));
    }

    [Fact]
    public void A_camera_sees_chunks_at_any_distance_by_default()
    {
        WriteFar(40);
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 40));

        Assert.True(Spawned("Test", 0, 0, 40));
    }

    [Fact]
    public void A_chunk_beyond_the_view_distance_is_not_streamed_in()
    {
        _project.Settings.Render.ViewDist = 200f;
        _project.Settings.Assets.Budgets[nameof(Chunk)] = 0; // nothing but what the camera wants is loaded
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 3));
        Step(10);

        Assert.False(Spawned("Test", 0, 0, 6));
    }

    [Fact]
    public void The_farthest_chunk_is_not_among_the_first_to_load()
    {
        // More chunks in view than any machine loads at once, so the last of them has to wait its turn.
        int farthest = 7 + Environment.ProcessorCount;
        WriteFar(7, count: farthest - 6);
        Open(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Stats.Loaded > 0);

        Assert.False(Spawned("Test", 0, 0, farthest));
    }

    [Fact]
    public void A_chunk_no_longer_seen_stays_within_the_chunk_budget()
    {
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        Step(200);

        Assert.True(Spawned("Test", 0, 0, 2));
    }

    [Fact]
    public void A_chunk_no_longer_seen_is_unloaded_when_over_the_chunk_budget()
    {
        _project.Settings.Assets.Budgets[nameof(Chunk)] = 0;
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        StepUntil(() => !Spawned("Test", 0, 0, 2));

        Assert.False(Spawned("Test", 0, 0, 2));
    }

    [Fact]
    public void A_chunk_within_the_radius_of_the_camera_stays_wherever_it_looks()
    {
        _project.Settings.Streaming.Radius = 64f;
        _project.Settings.Assets.Budgets[nameof(Chunk)] = 0;
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 1) && Spawned("Test", 0, 0, 2));
        TurnAround(camera);

        StepUntil(() => !Spawned("Test", 0, 0, 2)); // the far one has gone: nothing out of view is kept

        Assert.True(Spawned("Test", 0, 0, 1));
    }

    [Fact]
    public void A_far_chunk_is_spawned_as_its_stand_in()
    {
        WriteFar(40);
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 40));

        Assert.True(_ecs.Lookup("World.Test.Chunks.C0_0_40.StandIn").IsValid);
    }

    [Fact]
    public void A_stand_in_gives_way_to_its_chunk_when_a_camera_comes_near()
    {
        WriteFar(40);
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 40));

        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, (40 * 64) - 100);
        StepUntil(() => _ecs.Lookup("World.Test.Chunks.C0_0_40.Far").IsValid);

        Assert.False(_ecs.Lookup("World.Test.Chunks.C0_0_40.StandIn").IsValid);
    }

    [Fact]
    public void A_stand_in_stays_until_the_renderer_has_all_of_its_chunk()
    {
        WriteFar(40, content: Drawn);
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 40));

        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, (40 * 64) - 100);
        StepUntil(() => HiddenChunk().IsValid);

        Assert.True(_ecs.Lookup("World.Test.Chunks.C0_0_40.StandIn").IsValid);
    }

    [Fact]
    public void A_stand_in_gives_way_once_the_renderer_has_all_of_its_chunk()
    {
        WriteFar(40, content: Drawn);
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 40));
        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, (40 * 64) - 100);
        StepUntil(() => HiddenChunk().IsValid);

        Register(HiddenChunk());
        Step(1);

        Assert.False(_ecs.Lookup("World.Test.Chunks.C0_0_40.StandIn").IsValid);
    }

    [Fact]
    public void A_chunk_is_shown_once_the_renderer_has_all_of_it()
    {
        WriteFar(40, content: Drawn);
        Open(Test);
        Handle camera = SpawnCamera();
        StepUntil(() => Spawned("Test", 0, 0, 40));
        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, (40 * 64) - 100);
        StepUntil(() => HiddenChunk().IsValid);
        Handle chunk = HiddenChunk();

        Register(chunk);
        Step(1);

        Assert.False(_ecs.Has<Hidden>(chunk));
    }

    [Fact]
    public void A_chunk_gives_way_to_its_stand_in_when_the_camera_leaves()
    {
        WriteFar(40);
        Open(Test);
        Handle camera = SpawnCamera();
        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, (40 * 64) - 100);
        StepUntil(() => _ecs.Lookup("World.Test.Chunks.C0_0_40.Far").IsValid);

        _ecs.Get<Transform>(camera).Position = new Vector3(5, 5, 5);
        StepUntil(() => _ecs.Lookup("World.Test.Chunks.C0_0_40.StandIn").IsValid);

        Assert.False(_ecs.Lookup("World.Test.Chunks.C0_0_40.Far").IsValid);
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
        StepUntil(() => _streaming.Stats.Loaded == 8);

        Assert.Equal(8, _ecs.GetChildren(_ecs.Lookup("World.Test.Chunks")).Length);
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
    public void A_domain_is_loaded_once_the_chunks_the_camera_wants_are_spawned()
    {
        _project.Settings.Streaming.Radius = 0f;
        SpawnCamera();

        Open(Test);

        Assert.All(Enumerable.Range(0, 7), z => Assert.True(Spawned("Test", 0, 0, z)));
    }

    [Fact]
    public void A_chunk_no_camera_wants_is_loaded_while_the_chunk_budget_has_room()
    {
        _project.Settings.Streaming.Radius = 0f;
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, -2));

        Assert.True(Spawned("Test", 0, 0, -2));
    }

    [Fact]
    public void A_chunk_no_camera_wants_is_not_loaded_without_a_chunk_budget()
    {
        _project.Settings.Streaming.Radius = 0f;
        _project.Settings.Assets.Budgets[nameof(Chunk)] = 0;
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 6));
        Step(10);

        Assert.False(Spawned("Test", 0, 0, -2));
    }

    [Fact]
    public void A_camera_of_one_domain_streams_in_nothing_of_another()
    {
        Open(Watched);
        Open(Test);

        Step(10);

        Assert.Equal(0, _streaming.Stats.Loaded);
    }

    [Fact]
    public void A_domain_opened_behind_the_loading_domain_is_hidden()
    {
        OpenBehindLoading(Test);

        Step(1);

        Assert.True(_ecs.Has<Hidden>(_ecs.Lookup("World.Test")));
    }

    [Fact]
    public void A_domain_behind_the_loading_domain_is_loading_until_the_renderer_has_its_chunks()
    {
        OpenBehindLoading(Test);
        SpawnCamera();

        StepUntil(() => _streaming.Stats.Loaded == 6); // every chunk but the two that draw
        Step(10);

        Assert.Equal(DomainState.Loading, _world.StateOf(Test));
    }

    [Fact]
    public void A_domain_behind_the_loading_domain_is_loaded_once_the_renderer_has_its_chunks()
    {
        OpenBehindLoading(Test);
        SpawnCamera();
        StepUntil(() => _streaming.Stats.Loaded == 6);

        RegisterAll("Test");
        StepUntil(() => _world.StateOf(Test) == DomainState.Loaded);

        Assert.Equal(DomainState.Loaded, _world.StateOf(Test));
    }

    [Fact]
    public void A_domain_behind_the_loading_domain_is_shown_once_it_is_loaded()
    {
        OpenBehindLoading(Test);
        SpawnCamera();
        StepUntil(() => _streaming.Stats.Loaded == 6);

        RegisterAll("Test");
        StepUntil(() => _world.StateOf(Test) == DomainState.Loaded);

        Assert.False(_ecs.Has<Hidden>(_ecs.Lookup("World.Test")));
    }

    [Fact]
    public void A_domain_behind_the_loading_domain_is_loaded_with_the_chunks_no_camera_wants()
    {
        _project.Settings.Streaming.Radius = 0f;
        OpenBehindLoading(Test);
        SpawnCamera();
        StepUntil(() => _streaming.Stats.Loaded == 6);

        RegisterAll("Test");
        StepUntil(() => _world.StateOf(Test) == DomainState.Loaded);

        Assert.True(Spawned("Test", 0, 0, -2));
    }

    [Fact]
    public void The_loading_domain_is_closed_once_the_domain_behind_it_is_loaded()
    {
        OpenBehindLoading(Other);

        StepUntil(() => _world.StateOf(Other) == DomainState.Loaded);

        Assert.False(_ecs.Lookup("World.Loading").IsValid);
    }

    [Fact]
    public void An_opened_domain_is_loaded_in_the_world()
    {
        Open(Test);

        Assert.Equal(DomainState.Loaded, _world.StateOf(Test));
    }

    [Fact]
    public void A_loaded_domain_publishes_domain_loaded()
    {
        Open(Test);

        Step(1); // what one frame publishes the next one is given

        Assert.Contains(new Event(EventType.DomainLoaded, Id: Test.Id), _seen);
    }

    [Fact]
    public void A_loaded_domain_has_told_its_progress_one()
    {
        Told progress = new();

        _world.Open(Test, OpenMode.Additive, progress);
        StepUntil(() => _world.StateOf(Test) != DomainState.Loading);

        Assert.Equal(1f, progress.Values[^1]);
    }

    [Fact]
    public void A_domain_whose_changed_file_cannot_be_loaded_is_closed_in_the_world()
    {
        Open(Test);

        Write("Test/Test.domain", "{ not json", 3001);
        StepUntil(() => _world.StateOf(Test) == DomainState.Closed);

        Assert.Equal(DomainState.Closed, _world.StateOf(Test));
    }

    [Fact]
    public void A_domain_closed_before_a_frame_is_not_spawned()
    {
        _world.Open(Test);

        _world.Close(Test);
        Step(10);

        Assert.False(_ecs.Lookup("World.Test").IsValid);
    }

    [Fact]
    public void A_loaded_domain_stays_loaded_when_a_camera_outruns_loading()
    {
        WriteFar(40);
        Open(Test);
        SpawnCamera();

        Step(2); // the camera is placed in the first frame and seen in the second

        Assert.Equal(DomainState.Loaded, _world.StateOf(Test));
    }

    [Fact]
    public void A_chunk_too_big_to_spawn_in_one_frame_ends_up_with_every_entity()
    {
        string entities = string.Join(",", Enumerable.Range(0, 4000).Select(index => $$"""{ "name": "E{{index}}", "components": { "Transform": {} } }"""));
        WriteFar(8, content: $$"""{ "entities": [ {{entities}} ] }""");
        Open(Test);
        SpawnCamera();

        StepUntil(() => Spawned("Test", 0, 0, 8));

        Assert.Equal(4000, _ecs.GetChildren(_ecs.Lookup("World.Test.Chunks.C0_0_8")).Length);
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

    /// <summary>
    /// Opens the domain beside those open and steps until it is there: its globals and what the cameras there are now want.
    /// </summary>
    private void Open(Handle<Domain> domain)
    {
        _world.Open(domain, OpenMode.Additive);
        StepUntil(() => _world.StateOf(domain) != DomainState.Loading);
    }

    private void Close(Handle<Domain> domain)
    {
        _world.Close(domain);
        Step(1);
    }

    /// <summary>
    /// Opens the domain in place of whatever is open, behind the Loading domain, as a game opens its entry point.
    /// </summary>
    private void OpenBehindLoading(Handle<Domain> domain)
    {
        _world.Loading = Loading;
        _world.Open(domain);
    }

    /// <summary>
    /// What the renderer does to every chunk of the domain that is there, shown or not.
    /// </summary>
    private void RegisterAll(string domain)
    {
        foreach (Handle chunk in _ecs.GetChildren(_ecs.Lookup($"World.{domain}.Chunks")))
            Register(chunk);
    }

    /// <summary>
    /// Whether a domain's chunk is in the ECS now, where the system spawns it: <c>World.&lt;Domain&gt;.Chunks.Cx_y_z</c>.
    /// </summary>
    private bool Spawned(string domain, int x, int y, int z)
    {
        return _ecs.Lookup($"World.{domain}.Chunks.C{x}_{y}_{z}").IsValid;
    }

    /// <summary>
    /// A camera in chunk 0_0_0 looking down +Z.
    /// </summary>
    private Handle SpawnCamera()
    {
        Handle camera = _ecs.Create("Camera");
        _ecs.Set(camera, new Transform { Position = new Vector3(5, 5, 5) });
        _ecs.Set(camera, Camera.Perspective(60f, 0.1f));

        return camera;
    }

    /// <summary>
    /// The chunk spawned hidden, to take the place of one on screen; none when there is no such chunk.
    /// </summary>
    private Handle HiddenChunk()
    {
        return _ecs.GetChildren(_ecs.Lookup("World.Test.Chunks")).FirstOrDefault(chunk => _ecs.Has<Hidden>(chunk));
    }

    /// <summary>
    /// What the renderer does to every entity of the chunk that draws, once it has it.
    /// </summary>
    private void Register(Handle chunk)
    {
        foreach (Handle entity in _ecs.GetChildren(chunk))
            _ecs.Set(entity, new RenderInstance { Handle = 1 });
    }

    private void TurnAround(Handle camera)
    {
        _ecs.Get<Transform>(camera).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
    }

    /// <summary>
    /// What the frame loop does for these systems, <paramref name="frames"/> times: changes, then Update, then LateUpdate.
    /// </summary>
    private void Step(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _assets.ProcessChanges();
            Frame frame = new(1, 0, 1f / 60f, _events.NextFrame(), new RenderCommands());
            _seen.AddRange(frame.Events.Span);
            _ecs.Update(frame);
            _streaming.Run(frame);
            _ecs.LateUpdate(frame);
            _transforms.Run(frame);
        }
    }

    /// <summary>
    /// Steps until <paramref name="condition"/> holds, giving the workers and the file watcher up to five seconds.
    /// </summary>
    private void StepUntil(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Step(1);
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// More chunks of the Test domain's row, <paramref name="count"/> of them from 0_0_<paramref name="z"/> on, all
    /// written first so the watcher settles them together, and indexed before this returns.
    /// </summary>
    private void WriteFar(int z, int count = 1, string content = """{ "entities": [ { "name": "Far", "components": { "Transform": {} } } ] }""")
    {
        int[] rows = [.. Enumerable.Range(z, count)];
        foreach (int row in rows)
            Write($"Test/0_0_{row}.chunk", content, 4000ul + (ulong)row);

        Stopwatch clock = Stopwatch.StartNew();
        while (!rows.All(row => _assets.Find<Chunk>($"Domains/Test/0_0_{row}.chunk").IsValid) && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
            Step(1);
        }
    }

    private void Write(string file, string json, ulong id)
    {
        _root.Write($"Assets/Domains/{file}", json);
        _root.Write($"Assets/Domains/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }

    /// <summary>
    /// Every chunk's stand-in, beyond 512 m: one entity named StandIn.
    /// </summary>
    private sealed class StandIns : ILODGenerator<Chunk>
    {
        public int Version => 1;

        public async Task<Result<Dictionary<float, string>>> GenerateAsync(Chunk asset, string folder, IProgress<float>? progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "standin.chunk"), """{ "entities": [ { "name": "StandIn", "components": { "Transform": {} } } ] }""", cancel);

            return Result<Dictionary<float, string>>.Success(new() { [512f] = "standin.chunk" });
        }
    }

    /// <summary>
    /// Keeps what it is told, in order.
    /// </summary>
    private sealed class Told : IProgress<float>
    {
        public List<float> Values { get; } = [];

        public void Report(float value)
        {
            Values.Add(value);
        }
    }
}
