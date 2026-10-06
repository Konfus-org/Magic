using FlecsGem;
using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using ScriptingGem;
using Magic.Utils;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Xunit;
using Magic.Contexts.Domain;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The script system on the real Flecs gem, over script files in a temp folder. A loader stands in for the C#
/// script loader: a script's class is the nested class here named like its file. The scripts write what happens to
/// them in a <see cref="Journal"/> they are constructed with. The world holds its scripts while the Test domain is
/// open behind the Loading domain; both are empty domains here, opened for that alone.
/// </summary>
public sealed class ScriptSystemTests : IDisposable
{
    private const string OneProbe = """[ { "id": 1 } ]""";
    private const string OneTicker = """[ { "id": 2 } ]""";
    private const string OneCounter = """[ { "id": 5 } ]""";

    private static readonly Handle<Domain> Test = new(3001);
    private static readonly Handle<Domain> Loading = new(3060);

    private readonly TempFolder _root = new();
    private readonly FlecsEcs _ecs = new();
    private readonly Scheduler _scheduler = new();
    private readonly Journal _journal = new();
    private readonly Services.Assets _assets;
    private readonly World _world;
    private readonly ScriptSystem _scripts;

    public ScriptSystemTests()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources") };
        Write("Probe.cs", 1);
        Write("Ticker.cs", 2);
        Write("Thrower.cs", 3);
        Write("Needy.cs", 4);
        Write("Counter.cs", 5);
        Write("Constant.cs", 6);
        Write("Early.cs", 7);
        Write("Mover.lua", 8);
        WriteDomain("Test", 3001);
        WriteDomain("Loading", 3060);

        Container container = new();
        container.Add<IAssetLoader<Script>>(new NestedClasses());
        container.Add(_journal);
        _assets = new Services.Assets(project, new FileSystem(), new Events(), container, new Threads());
        _world = new World(new Events(), _assets, new Threads());
        _scripts = new ScriptSystem(_ecs, _assets, _world, _scheduler, container);

        // Loaded already, as a chunk's load leaves the scripts its entities carry: they are made in the first frame.
        for (ulong id = 1; id <= 7; id++)
            _assets.Load(new Handle<Script>(id));
    }

    public void Dispose()
    {
        _scripts.Dispose();
        _assets.Dispose();
        _ecs.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_behavior_is_made_with_its_entity()
    {
        Handle entity = _ecs.Create("Door");
        Attach(entity, OneProbe);

        _scripts.Run(FrameOf());

        Assert.Contains($"made {entity.Id}", _journal.Lines);
    }

    [Fact]
    public void A_script_is_not_made_while_the_world_loads()
    {
        HoldTheWorld();
        Attach(_ecs.Create(), OneProbe);

        _scripts.Run(FrameOf());

        Assert.Empty(_journal.Lines);
    }

    [Fact]
    public void A_script_is_made_once_the_world_has_loaded()
    {
        HoldTheWorld();
        Handle entity = _ecs.Create();
        Attach(entity, OneProbe);
        _scripts.Run(FrameOf());

        ReleaseTheWorld();
        _scripts.Run(FrameOf());

        Assert.Contains($"made {entity.Id}", _journal.Lines);
    }

    [Fact]
    public void A_script_that_runs_on_loading_is_made_while_the_world_loads()
    {
        HoldTheWorld();
        Attach(_ecs.Create(), """[ { "id": 7 } ]""");

        _scripts.Run(FrameOf());

        Assert.Contains("early", _journal.Lines);
    }

    [Fact]
    public void An_entry_whose_asset_is_not_a_cs_file_is_not_taken()
    {
        IDisposable? taken = Attach(_ecs.Create(), """[ { "id": 8 } ]""");

        Assert.Null(taken);
    }

    [Fact]
    public void An_entry_without_an_id_is_not_taken()
    {
        IDisposable? taken = Attach(_ecs.Create(), """[ { "speed": 3 } ]""");

        Assert.Null(taken);
    }

    [Fact]
    public void A_taken_entry_marks_its_entity_as_scripted()
    {
        Handle entity = _ecs.Create();

        Attach(entity, OneProbe);

        Assert.True(_ecs.Has<Scripts>(entity));
    }

    [Fact]
    public void Disposing_what_attach_answered_ends_the_script()
    {
        IDisposable? taken = Attach(_ecs.Create(), OneProbe);
        _scripts.Run(FrameOf());

        Assert.NotNull(taken);
        taken.Dispose();

        Assert.Contains("disposed", _journal.Lines);
    }

    [Fact]
    public void A_destroyed_parents_childs_behavior_is_disposed()
    {
        Handle parent = _ecs.Create();
        Attach(_ecs.Create(parent: parent), OneProbe);
        _scripts.Run(FrameOf());

        _ecs.Destroy(parent);
        _scripts.Run(FrameOf());

        Assert.Contains("disposed", _journal.Lines);
    }

    [Fact]
    public void A_value_beside_the_id_is_set_on_the_instance()
    {
        Attach(_ecs.Create(), """[ { "id": 1, "speed": 3 } ]""");

        _scripts.Run(FrameOf());

        Assert.Contains("update at speed 3", _journal.Lines);
    }

    [Fact]
    public void A_behavior_is_updated_when_the_system_runs()
    {
        Attach(_ecs.Create(), OneProbe);

        _scripts.Run(FrameOf());

        Assert.Contains("update at speed 1", _journal.Lines);
    }

    [Theory]
    [InlineData(UpdateType.FixedUpdate)]
    [InlineData(UpdateType.LateUpdate)]
    [InlineData(UpdateType.Render)]
    public void A_behavior_gets_the_hook_of_each_later_phase(UpdateType phase)
    {
        Attach(_ecs.Create(), OneProbe);

        RunPhase(phase);

        Assert.Contains(phase.ToString(), _journal.Lines);
    }

    [Fact]
    public void A_destroyed_entitys_behavior_is_disposed()
    {
        Handle entity = _ecs.Create();
        Attach(entity, OneProbe);
        _scripts.Run(FrameOf());

        _ecs.Destroy(entity);
        _scripts.Run(FrameOf());

        Assert.Contains("disposed", _journal.Lines);
    }

    [Fact]
    public void A_system_script_runs_in_its_phase()
    {
        Attach(_ecs.Create(), OneTicker);
        _scripts.Run(FrameOf());

        _ecs.Update(FrameOf());

        Assert.Contains("tick", _journal.Lines);
    }

    [Fact]
    public void A_destroyed_entitys_system_script_no_longer_runs()
    {
        Handle entity = _ecs.Create();
        Attach(entity, OneTicker);
        _scripts.Run(FrameOf());

        _ecs.Destroy(entity);
        _scripts.Run(FrameOf());
        _ecs.Update(FrameOf());

        Assert.DoesNotContain("tick", _journal.Lines);
    }

    [Fact]
    public void Changed_gems_make_every_instance_again()
    {
        Handle entity = _ecs.Create();
        Attach(entity, OneProbe);
        _scripts.Run(FrameOf());

        _scripts.Run(FrameOf(new Event(EventType.GemsChanged)));

        Assert.Equal(2, _journal.Lines.Count(line => line == $"made {entity.Id}"));
    }

    [Fact]
    public void A_behavior_that_throws_is_stopped()
    {
        Attach(_ecs.Create(), """[ { "id": 3 } ]""");
        _scripts.Run(FrameOf());

        _scripts.Run(FrameOf());

        Assert.Single(_journal.Lines);
    }

    [Fact]
    public void A_script_needing_what_nothing_provides_is_not_made()
    {
        Attach(_ecs.Create(), """[ { "id": 4 } ]""");

        _scripts.Run(FrameOf());

        Assert.Empty(_journal.Lines);
    }

    [Fact]
    public void A_behavior_near_a_camera_is_updated_every_frame()
    {
        Place(_ecs.Create("Camera"), Vector3.Zero, camera: true);
        Attach(Place(_ecs.Create(), new Vector3(0, 0, 10)), OneCounter);

        RunFrames(32);

        Assert.Equal(32, _journal.Lines.Count(line => line.StartsWith("count")));
    }

    [Fact]
    public void A_behavior_far_from_every_camera_is_updated_less_often()
    {
        Place(_ecs.Create("Camera"), Vector3.Zero, camera: true);
        Attach(Place(_ecs.Create(), new Vector3(0, 0, 200)), OneCounter); // 128 to 256 m: every 4th frame

        RunFrames(32);

        Assert.Equal(8, _journal.Lines.Count(line => line.StartsWith("count")));
    }

    [Fact]
    public void A_behavior_updated_less_often_gets_the_time_since_its_last_update()
    {
        Place(_ecs.Create("Camera"), Vector3.Zero, camera: true);
        Attach(Place(_ecs.Create(), new Vector3(0, 0, 200)), OneCounter);

        RunFrames(32);

        Assert.Equal("count 4", _journal.Lines.Last(line => line.StartsWith("count")));
    }

    [Fact]
    public void A_behavior_however_far_is_updated_within_the_longest_interval()
    {
        Place(_ecs.Create("Camera"), Vector3.Zero, camera: true);
        Attach(Place(_ecs.Create(), new Vector3(0, 0, 100000)), OneCounter);

        RunFrames(32);

        Assert.Single(_journal.Lines, line => line.StartsWith("count"));
    }

    [Fact]
    public void A_behavior_marked_always_is_updated_every_frame_however_far()
    {
        Place(_ecs.Create("Camera"), Vector3.Zero, camera: true);
        Attach(Place(_ecs.Create(), new Vector3(0, 0, 200)), """[ { "id": 6 } ]""");

        RunFrames(32);

        Assert.Equal(32, _journal.Lines.Count(line => line == "constant"));
    }

    [Fact]
    public void A_behavior_on_an_entity_with_no_place_is_updated_every_frame()
    {
        Place(_ecs.Create("Camera"), new Vector3(0, 0, 1000), camera: true);
        Attach(_ecs.Create(), OneCounter);

        RunFrames(32);

        Assert.Equal(32, _journal.Lines.Count(line => line.StartsWith("count")));
    }

    /// <summary>
    /// Offers each entry of <paramref name="json"/> to the system, as the streaming system does; answers what the last one gave.
    /// </summary>
    private IDisposable? Attach(Handle entity, string json)
    {
        IDisposable? taken = null;
        foreach (JsonElement entry in Entries(json))
            taken = _scripts.Attach(entity, entry);

        return taken;
    }

    /// <summary>
    /// Opens the Test domain behind the Loading domain and leaves it loading, which holds every script not marked Impatient.
    /// </summary>
    private void HoldTheWorld()
    {
        _world.Loading = Loading;
        Assert.True(_world.Open(Test).Ok);
    }

    /// <summary>
    /// The Test domain is there: the Loading domain closes and the held scripts are made in the next run.
    /// </summary>
    private void ReleaseTheWorld()
    {
        _world.Set(Test, DomainState.Loaded);
    }

    /// <summary>
    /// Puts the entity at <paramref name="position"/> in the world, as the transform system would have.
    /// </summary>
    private Handle Place(Handle entity, Vector3 position, bool camera = false)
    {
        _ecs.Set(entity, new WorldTransform { Value = Matrix4x4.CreateTranslation(position) });
        if (camera)
            _ecs.Set(entity, Camera.Perspective(60f, 0.1f));

        return entity;
    }

    /// <summary>
    /// The script system's Update for frames 1 to <paramref name="count"/>, each one second long.
    /// </summary>
    private void RunFrames(int count)
    {
        for (int number = 1; number <= count; number++)
            _scripts.Run(new Frame(number, number, 1f, ReadOnlyMemory<Event>.Empty, new RenderCommands()));
    }

    /// <summary>
    /// Runs one phase the way the frame loop does: the ECS gem's hook, which runs what the scheduler added.
    /// </summary>
    private void RunPhase(UpdateType phase)
    {
        _scheduler.SetFrame(FrameOf());
        switch (phase)
        {
            case UpdateType.FixedUpdate:
                _ecs.FixedUpdate(FrameOf());
                break;
            case UpdateType.LateUpdate:
                _ecs.LateUpdate(FrameOf());
                break;
            case UpdateType.Render:
                _ecs.Render(FrameOf());
                break;
        }
    }

    private static Frame FrameOf(params Event[] events)
    {
        return new Frame(1, 0, 1f / 60f, events, new RenderCommands());
    }

    private static JsonElement[] Entries(string json)
    {
        return JsonSerializer.Deserialize<JsonElement[]>(json) ?? [];
    }

    private void Write(string file, ulong id)
    {
        _root.Write($"Assets/Scripts/{file}", "");
        _root.Write($"Assets/Scripts/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }

    private void WriteDomain(string name, ulong id)
    {
        _root.Write($"Assets/Domains/{name}/{name}.domain", """{ "chunkSize": 64 }""");
        _root.Write($"Assets/Domains/{name}/{name}.domain.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }

    /// <summary>
    /// What the scripts under test did, in order.
    /// </summary>
    private sealed class Journal
    {
        public List<string> Lines { get; } = [];
    }

    /// <summary>
    /// The scripting gem's part: a script's class is the nested class of these tests named like its file.
    /// </summary>
    private sealed class NestedClasses : IAssetLoader<Script>
    {
        public Result Load(Script asset, byte[] bytes)
        {
            asset.Type = typeof(ScriptSystemTests).GetNestedType(asset.Name, BindingFlags.NonPublic);

            return Result.Success();
        }
    }

    [Impatient]
    private sealed class Early : IBehavior
    {
        public Early(Journal journal)
        {
            journal.Lines.Add("early");
        }
    }

    private sealed class Probe : IBehavior
    {
        public float Speed = 1f;

        private readonly Journal _journal;

        public Probe(Handle entity, Journal journal)
        {
            _journal = journal;
            journal.Lines.Add($"made {entity.Id}");
        }

        public void Dispose()
        {
            _journal.Lines.Add("disposed");
        }

        public void Update(in Frame frame)
        {
            _journal.Lines.Add($"update at speed {Speed}");
        }

        public void FixedUpdate(in Frame frame)
        {
            _journal.Lines.Add(nameof(UpdateType.FixedUpdate));
        }

        public void LateUpdate(in Frame frame)
        {
            _journal.Lines.Add(nameof(UpdateType.LateUpdate));
        }

        public void Render(in Frame frame)
        {
            _journal.Lines.Add(nameof(UpdateType.Render));
        }
    }

    private sealed class Ticker(Journal journal) : ISystem
    {
        public void Run(in Frame frame)
        {
            journal.Lines.Add("tick");
        }
    }

    private sealed class Thrower(Journal journal) : IBehavior
    {
        public void Update(in Frame frame)
        {
            journal.Lines.Add("about to throw");
            throw new InvalidOperationException("on purpose");
        }
    }

    /// <summary>
    /// Writes the delta of every update it gets.
    /// </summary>
    private sealed class Counter(Journal journal) : IBehavior
    {
        public void Update(in Frame frame)
        {
            journal.Lines.Add($"count {frame.Delta}");
        }
    }

    [AlwaysUpdate]
    private sealed class Constant(Journal journal) : IBehavior
    {
        public void Update(in Frame frame)
        {
            journal.Lines.Add("constant");
        }
    }

    /// <summary>
    /// Asks for a service the container does not have.
    /// </summary>
    private sealed class Needy : IBehavior
    {
        public Needy(Journal journal, IInput input)
        {
            journal.Lines.Add($"made with {input}");
        }
    }
}
