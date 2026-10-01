using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.Streaming;
using Magic.Utils;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The script system on the real Flecs gem, over script files in a temp folder. A loader stands in for the scripting
/// gem: a script's class is the nested class here named like its file. The scripts write what happens to them in a
/// <see cref="Journal"/> they are constructed with.
/// </summary>
public sealed class ScriptSystemTests : IDisposable
{
    private const string OneProbe = """[ { "id": 1 } ]""";
    private const string OneTicker = """[ { "id": 2 } ]""";

    private readonly TempFolder _root = new();
    private readonly FlecsEcs _ecs = new();
    private readonly Scheduler _scheduler = new();
    private readonly Journal _journal = new();
    private readonly Services.Assets _assets;
    private readonly ScriptSystem _scripts;

    public ScriptSystemTests()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, EngineGems = AppContext.BaseDirectory, Resources = Path.Combine(_root.Path, "NoResources") };
        Write("Probe.cs", 1);
        Write("Ticker.cs", 2);
        Write("Thrower.cs", 3);
        Write("Needy.cs", 4);

        Container container = new();
        container.Add<IAssetLoader<Script>>(new NestedClasses());
        container.Add(_journal);
        _assets = new Services.Assets(project, new FileSystem(), new Events(), container);
        _scripts = new ScriptSystem(_ecs, _assets, _scheduler, container);
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
        _scripts.Attach(entity, Entries(OneProbe));

        _scripts.Run(FrameOf());

        Assert.Contains($"made {entity.Id}", _journal.Lines);
    }

    [Fact]
    public void A_value_beside_the_id_is_set_on_the_instance()
    {
        _scripts.Attach(_ecs.Create(), Entries("""[ { "id": 1, "speed": 3 } ]"""));

        _scripts.Run(FrameOf());

        Assert.Contains("update at speed 3", _journal.Lines);
    }

    [Fact]
    public void A_behavior_is_updated_when_the_system_runs()
    {
        _scripts.Attach(_ecs.Create(), Entries(OneProbe));

        _scripts.Run(FrameOf());

        Assert.Contains("update at speed 1", _journal.Lines);
    }

    [Theory]
    [InlineData(UpdateType.FixedUpdate)]
    [InlineData(UpdateType.LateUpdate)]
    [InlineData(UpdateType.Render)]
    public void A_behavior_gets_the_hook_of_each_later_phase(UpdateType phase)
    {
        _scripts.Attach(_ecs.Create(), Entries(OneProbe));

        RunPhase(phase);

        Assert.Contains(phase.ToString(), _journal.Lines);
    }

    [Fact]
    public void A_destroyed_entitys_behavior_is_disposed()
    {
        Handle entity = _ecs.Create();
        _scripts.Attach(entity, Entries(OneProbe));
        _scripts.Run(FrameOf());

        _ecs.Destroy(entity);
        _scripts.Run(FrameOf());

        Assert.Contains("disposed", _journal.Lines);
    }

    [Fact]
    public void A_system_script_runs_in_its_phase()
    {
        _scripts.Attach(_ecs.Create(), Entries(OneTicker));
        _scripts.Run(FrameOf());

        _ecs.Update(FrameOf());

        Assert.Contains("tick", _journal.Lines);
    }

    [Fact]
    public void A_destroyed_entitys_system_script_no_longer_runs()
    {
        Handle entity = _ecs.Create();
        _scripts.Attach(entity, Entries(OneTicker));
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
        _scripts.Attach(entity, Entries(OneProbe));
        _scripts.Run(FrameOf());

        _scripts.Run(FrameOf(new Event(EventType.GemsChanged)));

        Assert.Equal(2, _journal.Lines.Count(line => line == $"made {entity.Id}"));
    }

    [Fact]
    public void A_behavior_that_throws_is_stopped()
    {
        _scripts.Attach(_ecs.Create(), Entries("""[ { "id": 3 } ]"""));
        _scripts.Run(FrameOf());

        _scripts.Run(FrameOf());

        Assert.Single(_journal.Lines);
    }

    [Fact]
    public void A_script_needing_what_nothing_provides_is_not_made()
    {
        _scripts.Attach(_ecs.Create(), Entries("""[ { "id": 4 } ]"""));

        _scripts.Run(FrameOf());

        Assert.Empty(_journal.Lines);
    }

    /// <summary>Runs one phase the way the frame loop does: the ECS gem's hook, which runs what the scheduler added.</summary>
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
        return new Frame(1, 0, 1f / 60f, events);
    }

    private static JsonElement[] Entries(string json)
    {
        return JsonSerializer.Deserialize<JsonElement[]>(json)!;
    }

    private void Write(string file, ulong id)
    {
        _root.Write($"Assets/Scripts/{file}", "");
        _root.Write($"Assets/Scripts/{file}.meta", $$$"""{ "id": {{{id}}}, "version": 1 }""");
    }

    /// <summary>What the scripts under test did, in order.</summary>
    private sealed class Journal
    {
        public List<string> Lines { get; } = [];
    }

    /// <summary>The scripting gem's part: a script's class is the nested class of these tests named like its file.</summary>
    private sealed class NestedClasses : IAssetLoader<Script>
    {
        public Result Load(Script asset, byte[] bytes)
        {
            asset.Type = typeof(ScriptSystemTests).GetNestedType(asset.Name, BindingFlags.NonPublic);

            return Result.Success();
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

    /// <summary>Asks for a service the container does not have.</summary>
    private sealed class Needy : IBehavior
    {
        public Needy(Journal journal, IInput input)
        {
            journal.Lines.Add($"made with {input}");
        }
    }
}
