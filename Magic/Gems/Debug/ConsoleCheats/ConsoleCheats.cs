using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Domain;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Globalization;
using System.Numerics;

namespace ConsoleCheatsGem;

/// <summary>
/// The gem: the engine's console commands for moving around and trying things live (<c>portal</c>, <c>summon</c>,
/// <c>exit</c>), added to <see cref="IConsoleCommands"/>.
/// </summary>
internal sealed class ConsoleCheats : IGem
{
    private const float SummonDistance = 3f; // metres in front of the camera a summoned thing lands, when no position is given

    private readonly World _world;
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Subscription[] _cheatRegistrations;

    public ConsoleCheats(World world, IEcs ecs, Assets assets, IConsoleCommands commands)
    {
        _world = world;
        _ecs = ecs;
        _assets = assets;
        _cheatRegistrations =
        [
            commands.Add("portal", Portal),
            commands.Add("summon", Summon),
            commands.Add("exit", _ => world.End()),
        ];
    }

    public void Dispose()
    {
        foreach (Subscription registration in _cheatRegistrations)
            registration.Dispose();
    }

    /// <summary>
    /// <c>portal</c> lists the domains there are. <c>portal &lt;domain&gt; [x,y,z] [additive]</c> opens one, named by
    /// its name, its file name or its path, in place of the world (or on top of it with <c>additive</c>), and once it
    /// is loaded puts the cameras that draw the main window at the position. <c>portal x,y,z</c> moves them there now.
    /// Replacing the world also takes what was summoned with it.
    /// </summary>
    private void Portal(string[] args)
    {
        string? name = null;
        Vector3? at = null;
        bool additive = false;
        foreach (string arg in args)
        {
            if (arg.Equals("additive", StringComparison.OrdinalIgnoreCase))
                additive = true;
            else if (TryParsePosition(arg, out Vector3 parsed))
                at = parsed;
            else if (name is null)
                name = arg;
            else
            {
                Log.Warn("Usage: portal [domain] [x,y,z] [additive]");
                return;
            }
        }

        if (name is null && at is null)
        {
            string[] domains = _assets.Paths(".domain");
            Log.Info(domains.Length == 0 ? "There are no domains." : $"Domains: {string.Join(", ", domains.Select(path => $"{Path.GetFileNameWithoutExtension(path)} ({path})"))}");
            return;
        }

        if (name is null)
        {
            PlaceCameras(at.GetValueOrDefault(), Handle<Domain>.None);
            return;
        }

        if (Resolve(name, ".domain") is not { } path)
            return;

        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (!additive)
            DestroySummoned();

        Result opened = _world.Open(domain, additive ? OpenMode.Additive : OpenMode.Replace);
        if (opened.Failed)
        {
            Log.Warn($"Portal to {path} failed: {opened.Message}");
            return;
        }

        Log.Info($"Portal to {path}{(additive ? " (additive)" : "")}.");
        if (at is { } position)
            _world.WhenLoaded([domain], () => PlaceCameras(position, domain));
    }

    /// <summary>
    /// <c>summon &lt;asset&gt; [x,y,z]</c>: the asset, named by its name, its file name or its path, put in the world
    /// at the position, or <see cref="SummonDistance"/> in front of the camera that draws the main window. A model
    /// comes as an entity wearing the default material, a material on a cube, a chunk as its entities under a root at
    /// the position, a domain opened on top of the world (it has no position). Other assets are not things.
    /// </summary>
    private void Summon(string[] args)
    {
        Vector3? at = null;
        bool usable = args.Length is 1 or 2;
        if (usable && args.Length == 2)
        {
            usable = TryParsePosition(args[1], out Vector3 given);
            at = given;
        }

        if (!usable)
        {
            Log.Warn("Usage: summon <asset> [x,y,z]");
            return;
        }

        if (Resolve(args[0], "") is not { } path)
            return;

        Vector3 position = at ?? InFrontOfCamera();
        string name = Path.GetFileNameWithoutExtension(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".chunk":
                _world.Spawn(_assets.Find<Chunk>(path), position);
                Log.Info($"Summoning {path} at {position}.");
                return;
            case ".domain":
                Result opened = _world.Open(_assets.Find<Domain>(path), OpenMode.Additive);
                Log.Info(opened.Ok ? $"Summoned {path} on top of the world; a domain is not placed." : $"Summon of {path} failed: {opened.Message}");
                return;
            case ".mat":
                SpawnModel(name, _assets.Default<Model>(), _assets.Find<Material>(path), position);
                return;
            case ".cs" or ".hlsl" or ".hlsli" or ".pass" or ".post" or ".pipeline" or ".preset" or ".icon" or ".rtex" or ".png" or ".jpg" or ".jpeg" or ".svg" or ".otf" or ".ttf" or ".txt":
                Log.Warn($"{path} cannot be summoned: it is not a thing in the world.");
                return;
        }

        Handle<Model> model = _assets.Find<Model>(path);
        if (_assets.Load(model) is null)
        {
            Log.Warn($"{path} cannot be summoned: it does not load as a model.");
            return;
        }

        SpawnModel(name, model, _assets.Default<Material>(), position);
    }

    /// <summary>
    /// The one asset <paramref name="name"/> means among those with <paramref name="extension"/> ("" for any): its path,
    /// its file name, or its name without the extension, in that order of exactness. None, or several, is said and null.
    /// </summary>
    private string? Resolve(string name, string extension)
    {
        string[] paths = [.. _assets.Paths(extension)];
        string wanted = name.Replace('\\', '/');

        string[] matches = [.. paths.Where(path => path.Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 0)
            matches = [.. paths.Where(path => Path.GetFileName(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 0)
            matches = [.. paths.Where(path => Path.GetFileNameWithoutExtension(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))];

        if (matches.Length == 1)
            return matches[0];

        Log.Warn(matches.Length == 0
            ? $"No asset is named {name}."
            : $"{matches.Length} assets are named {name}: {string.Join(", ", matches)}. Say which with its extension, or its path.");
        return null;
    }

    /// <summary>
    /// Puts every camera that draws the main window, under <paramref name="domain"/>'s root when one is given, at
    /// <paramref name="position"/>; where it looks stays.
    /// </summary>
    private void PlaceCameras(Vector3 position, Handle<Domain> domain)
    {
        Handle root = domain.IsValid && _assets.Load(domain) is { } loaded ? _ecs.Lookup($"{World.RootName}.{loaded.Name}") : default;
        List<Handle> cameras = [];
        using IEcsQuery<Transform, Camera> query = _ecs.Query<Transform, Camera>().Build();
        query.Each((Handle entity, ref Transform _, ref Camera camera) =>
        {
            if (camera.Target == RenderTarget.MainWindow && (!root.IsValid || IsUnder(entity, root)))
                cameras.Add(entity);
        });

        foreach (Handle camera in cameras)
        {
            ref Transform transform = ref _ecs.Get<Transform>(camera);
            transform.Position = position;
        }

        Log.Info(cameras.Count == 0 ? "No camera draws the main window; nothing was moved." : $"{cameras.Count} camera(s) put at {position}.");
    }

    private bool IsUnder(Handle entity, Handle root)
    {
        for (Handle at = _ecs.GetParent(entity); at.IsValid; at = _ecs.GetParent(at))
        {
            if (at == root)
                return true;
        }

        return false;
    }

    /// <summary>
    /// <see cref="SummonDistance"/> ahead of the first camera that draws the main window; the origin without one.
    /// </summary>
    private Vector3 InFrontOfCamera()
    {
        Vector3? found = null;
        using IEcsQuery<WorldTransform, Camera> query = _ecs.Query<WorldTransform, Camera>().Build();
        query.Each((Handle _, ref WorldTransform world, ref Camera camera) =>
        {
            if (found is null && camera.Target == RenderTarget.MainWindow)
                found = world.Value.Translation + (Vector3.Normalize(world.Value.Forward) * SummonDistance);
        });

        return found ?? Vector3.Zero;
    }

    /// <summary>
    /// An entity drawing <paramref name="model"/> in <paramref name="material"/> at <paramref name="position"/>, under World.Summoned.
    /// </summary>
    private void SpawnModel(string name, Handle<Model> model, Handle<Material> material, Vector3 position)
    {
        Handle entity = _ecs.Create(null, SummonedRoot());
        _ecs.SetName(entity, name);
        _ecs.Set(entity, new Transform { Position = position });
        _ecs.Add<WorldTransform>(entity);
        _ecs.Set(entity, new Renderer { Model = model, Materials = MaterialSlots.Of(material) });
        Log.Info($"Summoned {name} at {position}.");
    }

    /// <summary>
    /// World.Summoned, where what is summoned lives; made when there is none, as is World.
    /// </summary>
    private Handle SummonedRoot()
    {
        Handle world = _ecs.Lookup(World.RootName);
        if (!world.IsValid)
            world = _ecs.Create(World.RootName);

        Handle summoned = _ecs.Lookup($"{World.RootName}.{World.SummonedName}");
        return summoned.IsValid ? summoned : _ecs.Create(World.SummonedName, world);
    }

    private void DestroySummoned()
    {
        Handle summoned = _ecs.Lookup($"{World.RootName}.{World.SummonedName}");
        if (summoned.IsValid && _ecs.IsAlive(summoned))
            _ecs.Destroy(summoned);
    }

    /// <summary>
    /// <c>x,y,z</c> as a position; false for anything else.
    /// </summary>
    private static bool TryParsePosition(string text, out Vector3 position)
    {
        position = default;
        string[] parts = text.Split(',');
        if (parts.Length != 3)
            return false;

        float[] values = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        position = new Vector3(values[0], values[1], values[2]);
        return true;
    }
}
