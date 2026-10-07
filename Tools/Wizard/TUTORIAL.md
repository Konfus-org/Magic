# Standing up Wizard

A step-by-step build of the Magic editor, **Wizard**, and the MCP server that lets it (and an LLM) drive the engine.
Work through it top to bottom. Every part ends with a checkpoint you can run before moving on.

All paths are relative to the repo root (the folder holding `Magic-Wizard.slnx`) unless a step says otherwise.
Commands are PowerShell.

## What you are building

```
 Wizard desktop app                              Magic.exe
 ┌────────────────────────────────────┐          ┌───────────────────────────────┐
 │ Electron shell                     │          │ Core  (knows nothing of any   │
 │   (Wizard\Shell)                   │          │        editor)                │
 │   window, menu, native dialogs     │          │                               │
 │        │ spawns, shows its URL     │          │ Magic\Gems\Mcp  (static gem)  │
 │        ▼                           │  spawns  │   HttpListener   pool threads │
 │ Wizard server  (Wizard\Wizard)     │─────────▶│   Threads ──▶ main thread     │
 │   Blazor Server, all logic in C#   │          │   tools over IEcs, World,     │
 │   Engine service ── MCP client ────┼── HTTP ─▶│   Assets, Settings, Log       │
 └────────────────────────────────────┘ localhost│                               │
 Claude Code ── .mcp.json ──────────────────────▶│                               │
                                                 └───────────────────────────────┘
```

Three processes, two boundaries:

- **Engine ↔ everything else is MCP.** A new gem, `Mcp`, serves tools (`entity_get`, `component_set`, `log_read`, …)
  over localhost HTTP. Wizard references no Magic assembly. A tool name and JSON are the whole contract, and an LLM
  gets exactly the same one.
- **Shell ↔ Wizard is a URL.** The Wizard server is an ordinary Blazor Server app. Electron starts it, reads the
  address it prints, and shows it in a native window. You can also open that address in a browser tab, which is how
  you iterate with hot reload.

By the end you can: press Play in Wizard to launch the engine on a project, watch its log, run console commands,
browse the entity hierarchy, edit components live in an inspector, and do all of that from Claude Code as well.

### Decisions baked in, and why

| Decision | Why |
|---|---|
| MCP server is a gem, using `ModelContextProtocol.Core` over `HttpListener` | `Magic.exe` only has the base .NET runtime, so ASP.NET Core cannot load from a gem. The Core package plus the BCL's `HttpListener` needs nothing extra. |
| Stateless: no server push, clients poll (`log_read(after)`) | One request, one answer fits a main-thread engine, and it is the MCP SDK's default HTTP mode. |
| The gem only listens when `MAGIC_MCP_PORT` is set | A game never opens a port by accident, and Core needs no MCP option. Wizard sets it on the engine it starts. |
| Component access by reflection inside the gem | `IEcs` is generic-only (`Get<T>`). The gem closes those generics over each component type rather than widening `IEcs` for one caller. |
| Entity ids travel as strings | They are 64-bit; JSON numbers lose precision past 2^53. |
| Blazor Server inside official Electron | C# logic, web UI, cross-platform, and both halves have large backing. No community wrapper (Electron.NET) in between. |
| The engine keeps its own window | No viewport work in this pass. An in-editor viewport means streaming frames and is its own project. |

### What was verified, and what was not

The whole tutorial was first built and run on this machine (Windows 11, .NET SDK 10.0.401, Node 24.19, Electron 44.5)
against an earlier layout of the engine: the gem's 20 tools from a C# MCP client, Wizard in a browser (Play, Stop,
console, hierarchy, inspector edits, add entity, add component), the Electron shell from source, attached to a
running server (`WIZARD_URL`) and packaged, and closing the window stopping the Wizard server and the engine.

The engine has been restructured since: first into Core and contract gems, with services instead of statics, for
which the gem in Part 2 was rewritten, then with every contract folded into Core. The rewritten gem was built, with
no errors or warnings of its own, in a copy of the repo, and its tools were called by hand (Part 3) against a
`--headless` run of the Samples project: `engine_info`, `log_read`,
`command_list`, `command_run`, `entity_children`, `entity_get`, `component_set`, `component_types`,
`settings_get`, `settings_set`, `asset_list`, `domain_open`, `screenshot` (which answers an error when headless)
and `quit`, plus a refused request from a browser `Origin`.

Not verified, so treat as "should work":

- **The gem since the contracts folded into Core.** Their types kept their namespaces, so only its project
  references changed (Core alone), but it was not rebuilt.
- **Parts 4 to 9 after the restructure.** Wizard references no engine code, so only its engine paths changed, but
  it was not rebuilt.
- **Claude Code connecting through `.mcp.json`.** The server answers a standard MCP `initialize` and `tools/list`
  from both a raw HTTP client and the official SDK client, but I could not add a server to a Claude Code session
  from inside one.
- **macOS and Linux.** Nothing in Wizard is Windows-specific; the engine runs on Windows and Linux, but `HttpListener`
  and the shell were only tried on Windows.
- **`dotnet watch` hot reload** was not exercised; it is the stock Blazor Server workflow.

## Part 0: Prerequisites

You already have both:

```powershell
dotnet --version   # 10.0.401 or later 10.x
node --version     # v24 or later; npm comes with it
```

No workloads to install. Before you start, make sure the engine builds and the Samples project runs (it opens the
Hub):

```powershell
dotnet build Magic-Wizard.slnx
Build\net10.0\Debug\bin\Magic.exe --project Samples
```

## Part 1: What the gem needs from the engine

The gem needs three things from the engine. Two are already there; the third is a one-line build change. All three
are general-purpose: anything that drives the engine from outside the console window needs them, editor or not.

### 1.1 Console commands are already open to gems

Nothing to change: the `DebugUI` service (`Magic\Core\Services\Debug\DebugUI.cs`) lists the console commands
(`Commands`) and runs one (`Run`) in public. The Mcp gem takes `DebugUI` in its constructor below.

### 1.2 Components are already read and written the way chunk files do

Nothing to change: `AssetJson` (`Magic\Core\Services\AssetJson.cs`, namespace `Magic.Services`) is public.
The gem serialises components with its options, so what `entity_get` returns is exactly what a `.chunk` file
holds, and what `component_set` accepts is exactly what a `.chunk` file may contain.

### 1.3 Pin ZLoggerLogging's logging packages

All engine gems build flat into `Build\net10.0\Debug\bin\Gems\`. ZLogger brings
`Microsoft.Extensions.Logging.Abstractions.dll` version 8; the MCP SDK brings version 10 of the same file. Whichever
gem builds last would win, and the loser risks a `MissingMethodException`. Make both resolve the same version.

`Magic\Gems\Debug\ZLoggerLogging\ZLoggerLogging.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="ZLogger" Version="2.5.10" />
    <!-- Same version the Mcp gem's SDK pulls in: gems share one flat output folder, so two versions of
         Microsoft.Extensions.Logging.Abstractions.dll would overwrite each other. -->
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.10" />
  </ItemGroup>
```

### Checkpoint

```powershell
dotnet build Magic\Core\Core.csproj
```

It builds with no errors (building Core builds every default gem too). You can confirm the pin after Part 2: every
`Microsoft.Extensions.*.dll` in `Build\net10.0\Debug\bin\Gems\` shows file version 10.0.x.

## Part 2: The Mcp gem

### 2.1 The project

Create the folder `Magic\Gems\Mcp` and in it `Mcp.csproj`. It has the shape every gem's csproj has
(`Tools\VSTemplates\MagicGem\MyMagicGem.csproj`): it references Core only, without copying it. Everything it uses
(`IEcs`, `World`, `Assets`, `DebugUI`, `IRendering`, `IWindowRegistry`) is Core's, in `Magic.dll`.

```xml
<Project>

  <!-- Must come before the SDK props import: the Directory.Build.props of the engine repo, or of a Magic project,
       switches this project to gem output settings when IsMagicGem is true. -->
  <PropertyGroup>
    <IsMagicGem>true</IsMagicGem>
  </PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- The namespace is McpGem, not Mcp: the gem class is called Mcp. -->
    <RootNamespace>McpGem</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.Core" Version="2.2.0" />
  </ItemGroup>

  <!-- Inside the engine repo the gem compiles against Core as a project reference. Inside a Magic project
       (MagicProjectRoot is set by the project's Directory.Build.props) that file references the engine's built
       Magic.dll instead, so nothing is added here. Either way Magic.dll is never copied next to the gem: the
       gem's load context shares the host's already-loaded copy, and a second copy would be a different
       assembly identity whose IGem the host would not recognise. -->
  <ItemGroup Condition="'$(MagicProjectRoot)' == ''">
    <ProjectReference Include="$(MagicRoot)Magic\Core\Core.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

  <!-- The gem, as the engine sees it before constructing it. GemStatic true: loaded once, never hot reloaded. -->
  <PropertyGroup>
    <GemStatic>true</GemStatic>
    <Description>Serves the engine to MCP clients (editors, LLMs) over localhost HTTP.</Description>
    <Authors>Konfus</Authors>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

</Project>
```

Why `GemStatic` is true: the gem owns a listening socket and requests in flight. Reloading it mid-call would drop
them, and the gems it talks to through Core's interfaces (the ECS, the renderer, the platform) are static anyway.

The gem is one file: its `IGem` class at the root of the folder, as every part is laid out. It makes nothing
public, so it has no `Contexts` or `Services` folder.

### 2.2 Register it with the build

`Magic\Core\Core.csproj`, at the end of the `DefaultGem` list:

```xml
    <DefaultGem Include="$(MagicRoot)Magic\Gems\World\LoadingScreen\LoadingScreen.csproj" />
    <DefaultGem Include="$(MagicRoot)Magic\Gems\Mcp\Mcp.csproj" />
```

`Magic-Wizard.slnx`, whose folders mirror the disk: a folder of its own, between ModelLodGenerator and
DeferredRenderer.

```xml
  <Folder Name="/Magic/Gems/LODs/ModelLodGenerator/">
    <Project Path="Magic/Gems/LODs/ModelLodGenerator/ModelLodGenerator.csproj" />
  </Folder>
  <Folder Name="/Magic/Gems/Mcp/">
    <Project Path="Magic/Gems/Mcp/Mcp.csproj" />
  </Folder>
  <Folder Name="/Magic/Gems/Rendering/DeferredRenderer/">
```

(`dotnet sln Magic-Wizard.slnx add Magic\Gems\Mcp\Mcp.csproj --solution-folder Magic\Gems\Mcp` does the same.)

### 2.3 The gem

Create `Magic\Gems\Mcp\Mcp.cs`. Read the notes after the listing before you type it in; they explain the parts that
are not obvious.

```csharp
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Domain;
using Magic.Contexts.Rendering;
using Magic.Contexts.Threading;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IComponent = Magic.Contexts.Components.IComponent;

namespace McpGem;

/// <summary>
/// Serves the engine to MCP clients (an editor, an LLM) over localhost HTTP, so they can drive it without the engine
/// knowing who they are. Off unless the <c>MAGIC_MCP_PORT</c> environment variable names a port: a game never opens
/// one by accident. Requests arrive on pool threads; the engine is main-thread only, so every tool that touches it
/// is handed to the main thread (<see cref="Threads"/>) and answers once that has run it. Stateless: one request, one
/// answer, nothing pushed, so clients poll (<c>log_read</c>). It hears of every component type as gems load and
/// unload (a registrar of <see cref="IComponent"/>), and of every log line (an <see cref="ILogger"/>).
/// </summary>
internal sealed class Mcp : IGem, ILogger, ITypeRegistrar<IComponent>
{
    private const int LogCapacity = 2000;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly MethodInfo _has = EcsMethod(nameof(IEcs.Has));
    private static readonly MethodInfo _tryGet = EcsMethod(nameof(IEcs.TryGet));
    private static readonly MethodInfo _set = EcsMethod(nameof(IEcs.Set));
    private static readonly MethodInfo _remove = EcsMethod(nameof(IEcs.Remove));

    private readonly IEcs _ecs;
    private readonly World _world;
    private readonly Assets _assets;
    private readonly Project _project;
    private readonly Settings _settings;
    private readonly Threads _threads;
    private readonly DebugUI _debug;
    private readonly IRendering? _rendering;
    private readonly IWindowRegistry? _windows;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new(); // the log and the component types: both are written from other threads
    private readonly Queue<LogLine> _log = new();
    private readonly Dictionary<string, Type> _componentTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly McpServerOptions _options;
    private long _lastLogId;
    private long _frame;

    public Mcp(IEcs ecs, World world, Assets assets, Project project, Settings settings, Threads threads, DebugUI debug, IRendering? rendering, IWindowRegistry? windows)
    {
        _ecs = ecs;
        _world = world;
        _assets = assets;
        _project = project;
        _settings = settings;
        _threads = threads;
        _debug = debug;
        _rendering = rendering;
        _windows = windows;
        _options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Magic", Version = "1.0.0" },
            ServerInstructions = "Drives a running Magic engine. Poll log_read for output; commands and edits answer once the engine's next frame has run them.",
            ToolCollection =
            [
                Tool("engine_info", "The open project, the frame the engine is on, its entity count and the open domains.",
                    () => OnMainThreadAsync(EngineInfo)),
                Tool("log_read", "Log lines newer than 'after' (0 for all kept), oldest first. Pass the last id you saw to get only what is new.",
                    ([Description("Id of the last line already seen; 0 for everything kept.")] long after = 0,
                     [Description("Most lines to return.")] int max = 200) => LogRead(after, max)),
                Tool("command_list", "The names of every console command.",
                    () => OnMainThreadAsync(() => Serialize(_debug.Commands))),
                Tool("command_run", "Runs a console command. Its output goes to the log: read it with log_read.",
                    ([Description("The command's name, from command_list.")] string name,
                     [Description("Arguments, already split.")] string[]? args = null) => OnMainThreadAsync(() => CommandRun(name, args ?? []))),
                Tool("screenshot", "The main window as it was last shown, as a PNG.",
                    () => _threads.InvokeAsync(ThreadId.Render, _ => Screenshot(), _stopping.Token)),
                Tool("domain_open", "Opens a domain by asset path, such as Cave/Cave.domain.",
                    ([Description("Path relative to the project's Assets folder or a gem's Resources folder, forward slashes.")] string path,
                     [Description("True to open on top of what is open; false to replace it.")] bool additive = false) => OnMainThreadAsync(() => DomainOpen(path, additive))),
                Tool("domain_close", "Closes an open domain by asset path.",
                    ([Description("Path relative to the project's Assets folder or a gem's Resources folder, forward slashes.")] string path) => OnMainThreadAsync(() => DomainClose(path))),
                Tool("entity_children", "The entities directly under a parent: id, name, enabled and how many children each has.",
                    ([Description("The parent's id; leave out for the world root.")] string parent = "") => OnMainThreadAsync(() => EntityChildren(parent))),
                Tool("entity_get", "One entity: name, parent, enabled and every component it has, as JSON in the chunk file format.",
                    ([Description("The entity's id, from entity_children.")] string id) => OnMainThreadAsync(() => EntityGet(id))),
                Tool("entity_create", "Creates an entity and answers its id.",
                    ([Description("Unique among its siblings; leave out for none.")] string name = "",
                     [Description("The parent's id; leave out for a root entity.")] string parent = "") => OnMainThreadAsync(() => EntityCreate(name, parent))),
                Tool("entity_destroy", "Destroys an entity and all of its children.",
                    ([Description("The entity's id.")] string id) => OnMainThreadAsync(() => EntityDestroy(id))),
                Tool("entity_rename", "Renames an entity.",
                    ([Description("The entity's id.")] string id,
                     [Description("The new name, unique among its siblings.")] string name) => OnMainThreadAsync(() => EntityRename(id, name))),
                Tool("entity_enable", "Enables or disables an entity; a disabled one keeps its data but every system skips it.",
                    ([Description("The entity's id.")] string id,
                     [Description("False to disable.")] bool enabled = true) => OnMainThreadAsync(() => EntityEnable(id, enabled))),
                Tool("component_types", "The names of every component type an entity can have.",
                    () => Serialize(ComponentTypes().Select(pair => pair.Key))),
                Tool("component_set", "Adds or overwrites a component on an entity. The value is the whole component, as entity_get shows it; fields left out take their defaults.",
                    ([Description("The entity's id.")] string id,
                     [Description("The component's name, from component_types.")] string component,
                     [Description("The component as a JSON object.")] JsonElement value) => OnMainThreadAsync(() => ComponentSet(id, component, value))),
                Tool("component_remove", "Removes a component from an entity.",
                    ([Description("The entity's id.")] string id,
                     [Description("The component's name.")] string component) => OnMainThreadAsync(() => ComponentRemove(id, component))),
                Tool("settings_get", "Every settings class's values, by name (Gpu, Lod, World, ...).",
                    () => OnMainThreadAsync(SettingsGet)),
                Tool("settings_set", "Changes one setting, as the console's set command does: live until the next preset, never saved. A name that matches nothing is warned about in the log.",
                    ([Description("The settings name, such as Gpu.")] string section,
                     [Description("The setting in it, such as Vsync.")] string key,
                     [Description("The new value as JSON.")] JsonElement value) => OnMainThreadAsync(() => SettingsSet(section, key, value))),
                Tool("asset_list", "The asset paths directly in a folder with an extension.",
                    ([Description("Folder relative to the project's Assets folder or a gem's Resources folder, forward slashes.")] string folder,
                     [Description("Extension with its dot, such as .chunk.")] string extension) => AssetList(folder, extension)),
                Tool("quit", "Quits the engine once the current frame is done.",
                    () => OnMainThreadAsync(() =>
                    {
                        _world.End();
                        return "quitting";
                    })),
            ],
        };

        if (!int.TryParse(Environment.GetEnvironmentVariable("MAGIC_MCP_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            return;

        // Both names: HTTP.sys matches the Host header against the prefix, and clients use either.
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        ListenAsync().FireAndForget();
        Log.Info($"MCP server listening on http://localhost:{port}/mcp");
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }

    public void Update(in Frame frame)
    {
        _frame = frame.Number;
    }

    private static MethodInfo EcsMethod(string name)
    {
        return typeof(IEcs).GetMethod(name) ?? throw new MissingMethodException(nameof(IEcs), name);
    }

    private static McpServerTool Tool(string name, string description, Delegate run)
    {
        return McpServerTool.Create(run, new McpServerToolCreateOptions { Name = name, Description = description });
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, _json);
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // the listener was closed
            }

            Task.Run(() => ServeAsync(context)).FireAndForget();
        }
    }

    /// <summary>
    /// One POST is one JSON-RPC message; the answer is written back as a server-sent event, which is what Streamable HTTP asks for.
    /// </summary>
    private async Task ServeAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            // A web page in a browser could reach localhost too; only let through what did not come from one.
            if (request.Headers["Origin"] is { } origin && !(Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) && from.IsLoopback))
            {
                response.StatusCode = 403;
                return;
            }

            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "POST";
                return;
            }

            JsonRpcMessage? message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(request.InputStream, McpJsonUtilities.DefaultOptions, _stopping.Token).ConfigureAwait(false);
            if (message is null)
            {
                response.StatusCode = 400;
                return;
            }

            StreamableHttpServerTransport transport = new() { Stateless = true };
            await using ConfiguredAsyncDisposable disposingTransport = transport.ConfigureAwait(false);
            McpServer server = McpServer.Create(transport, _options);
            await using ConfiguredAsyncDisposable disposingServer = server.ConfigureAwait(false);
            using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            server.RunAsync(answered.Token).FireAndForget();

            // The answer is buffered so the status can still change: a notification has no answer, and that is a 202.
            using MemoryStream body = new();
            bool wrote = await transport.HandlePostRequestAsync(message, body, answered.Token).ConfigureAwait(false);
            await answered.CancelAsync().ConfigureAwait(false);
            if (!wrote)
            {
                response.StatusCode = 202;
                return;
            }

            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache, no-store";
            response.ContentLength64 = body.Length;
            body.Position = 0;
            await body.CopyToAsync(response.OutputStream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or ObjectDisposedException or HttpListenerException or IOException)
        {
            if (ex is JsonException)
                response.StatusCode = 400;
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // the client went away first
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread at the start of the next frame and hands back what it returned,
    /// or threw.
    /// </summary>
    private Task<T> OnMainThreadAsync<T>(Func<T> work)
    {
        return _threads.InvokeAsync(ThreadId.Main, _ =>
        {
            try
            {
                return work();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                throw new McpException(inner.Message); // a component call made through reflection: say what it said
            }
        }, _stopping.Token);
    }

    private string EngineInfo()
    {
        return Serialize(new
        {
            project = _project.Name,
            root = _project.Root,
            frame = _frame,
            entities = _ecs.EntityCount,
            domains = _world.Domains.Select(open => _assets.PathOf(open.Handle.Id)),
        });
    }

    private string LogRead(long after, int max)
    {
        lock (_lock)
            return Serialize(_log.Where(line => line.Id > after).Take(max <= 0 ? LogCapacity : max));
    }

    private string CommandRun(string name, string[] args)
    {
        if (!_debug.Run(name, args))
            throw new McpException($"There is no console command called '{name}'.");

        return "ran";
    }

    /// <summary>
    /// Render thread, which owns the GPU and the windows. Waits for the GPU.
    /// </summary>
    private ContentBlock Screenshot()
    {
        if (_rendering is not { } rendering || _windows?.Main is not { } window)
            throw new McpException("There is no renderer or no main window to capture.");

        Result<CapturedFrame> taken = rendering.Read(GpuTexture.Window(window.Handle));
        if (taken.Failed)
            throw new McpException($"Screenshot failed: {taken.Message}");

        CapturedFrame frame = taken.Payload;
        return ImageContentBlock.FromBytes(frame.Pixels.AsSpan().Png(frame.Width, frame.Height), "image/png");
    }

    private string DomainOpen(string path, bool additive)
    {
        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (!domain.IsValid)
            throw new McpException($"There is no domain at '{path}'.");

        if (_world.Open(domain, additive ? OpenMode.Additive : OpenMode.Replace) is { Failed: true } opened)
            throw new McpException(opened.Message);

        return "opened";
    }

    private string DomainClose(string path)
    {
        Handle<Domain> domain = _assets.Find<Domain>(path);
        if (!domain.IsValid)
            throw new McpException($"There is no domain at '{path}'.");

        _world.Close(domain);
        return "closed";
    }

    private string EntityChildren(string parent)
    {
        Handle handle = parent.Length == 0 ? _ecs.Lookup(World.RootName) : Entity(parent);
        if (!handle.IsValid)
            return "[]"; // nothing is open yet

        return Serialize(ChildrenOf(handle).Select(child => new
        {
            id = child.Id.ToString(CultureInfo.InvariantCulture),
            name = _ecs.GetName(child),
            enabled = _ecs.IsEnabled(child),
            children = ChildrenOf(child).Count,
        }));
    }

    private string EntityGet(string id)
    {
        Handle entity = Entity(id);
        JsonObject components = [];
        foreach ((string name, Type type) in ComponentTypes())
        {
            if (_has.MakeGenericMethod(type).Invoke(_ecs, [entity]) is not true)
                continue;

            // A tag has no value to read; an empty object says it is there.
            object?[] arguments = [entity, null];
            bool isTag = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
            if (!isTag)
                _tryGet.MakeGenericMethod(type).Invoke(_ecs, arguments);

            components[name] = isTag ? new JsonObject() : JsonSerializer.SerializeToNode(arguments[1], type, AssetJson.Compact);
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = _ecs.GetName(entity),
            ["parent"] = _ecs.GetParent(entity).Id.ToString(CultureInfo.InvariantCulture),
            ["enabled"] = _ecs.IsEnabled(entity),
            ["components"] = components,
        }.ToJsonString();
    }

    private string EntityCreate(string name, string parent)
    {
        Handle created = _ecs.Create(name.Length == 0 ? null : name, parent.Length == 0 ? Handle.None : Entity(parent));
        return created.Id.ToString(CultureInfo.InvariantCulture);
    }

    private string EntityDestroy(string id)
    {
        _ecs.Destroy(Entity(id));
        return "destroyed";
    }

    private string EntityRename(string id, string name)
    {
        _ecs.SetName(Entity(id), name);
        return "renamed";
    }

    private string EntityEnable(string id, bool enabled)
    {
        _ecs.Enable(Entity(id), enabled);
        return enabled ? "enabled" : "disabled";
    }

    private string ComponentSet(string id, string component, JsonElement value)
    {
        Handle entity = Entity(id);
        Type type = ComponentType(component);
        object parsed;
        try
        {
            parsed = value.Deserialize(type, AssetJson.Compact) ?? throw new JsonException("null");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new McpException($"That is not a {type.Name}: {ex.Message}");
        }

        _set.MakeGenericMethod(type).Invoke(_ecs, [entity, parsed]);
        return "set";
    }

    private string ComponentRemove(string id, string component)
    {
        _remove.MakeGenericMethod(ComponentType(component)).Invoke(_ecs, [Entity(id)]);
        return "removed";
    }

    private string SettingsGet()
    {
        JsonObject sections = [];
        foreach (object settings in _settings.All)
            sections[Settings.NameOf(settings)] = JsonSerializer.SerializeToNode(settings, settings.GetType(), AssetJson.Compact);

        return sections.ToJsonString();
    }

    private string SettingsSet(string section, string key, JsonElement value)
    {
        if (_settings.Set([$"{section}.{key}={value.GetRawText()}"]).Length > 0)
            throw new McpException($"'{section}.{key}' is not a setting's name.");

        return "set";
    }

    private string AssetList(string folder, string extension)
    {
        return Serialize(_assets.FindAll<Asset>(folder, extension).Select(asset => _assets.PathOf(asset.Id)));
    }

    /// <summary>
    /// The live entity <paramref name="id"/> names. Ids travel as strings: they are 64-bit and JSON numbers are not.
    /// </summary>
    private Handle Entity(string id)
    {
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) || !_ecs.IsAlive(new Handle(value)))
            throw new McpException($"There is no entity with id '{id}'.");

        return new Handle(value);
    }

    private List<Handle> ChildrenOf(Handle parent)
    {
        List<Handle> children = [];
        _ecs.GetChildren(parent, children);
        return children;
    }

    private Type ComponentType(string name)
    {
        lock (_lock)
        {
            if (_componentTypes.TryGetValue(name, out Type? type))
                return type;
        }

        throw new McpException($"There is no component type called '{name}'. component_types lists them.");
    }

    /// <summary>
    /// Every component type by name, in name order, as it is now.
    /// </summary>
    private KeyValuePair<string, Type>[] ComponentTypes()
    {
        lock (_lock)
            return [.. _componentTypes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)];
    }

    void ILogger.Log(LogLevel level, string message, string file, int line)
    {
        lock (_lock)
        {
            _log.Enqueue(new LogLine(++_lastLogId, level.ToString(), message));
            if (_log.Count > LogCapacity)
                _log.Dequeue();
        }
    }

    /// <summary>
    /// A component type, by its name, or its full name when another type already has the name.
    /// </summary>
    object? ITypeRegistrar.Register(Type type)
    {
        lock (_lock)
            _componentTypes[_componentTypes.ContainsKey(type.Name) ? type.FullName ?? type.Name : type.Name] = type;

        return null;
    }

    void ITypeRegistrar.Unregister(Type type)
    {
        lock (_lock)
        {
            foreach (string name in _componentTypes.Where(pair => pair.Value == type).Select(pair => pair.Key).ToArray())
                _componentTypes.Remove(name);
        }
    }

    private readonly record struct LogLine(long Id, string Level, string Message);
}
```

#### Notes on the gem

**The `using` alias.** `System.ComponentModel` (for `[Description]`) also has an `IComponent`. Without the alias
it is ambiguous and the build fails with CS0104.

**The engine's build rules apply.** The repo's `.editorconfig` and `Directory.Build.props` make these errors, which
is why the listing looks the way it does: every `await` says `.ConfigureAwait(false)` (an `await using` too, through
`ConfiguredAsyncDisposable`), a task nobody awaits is let go with `FireAndForget()` rather than `_ =`, the
null-forgiving `!` is banned, numbers are parsed culture-invariantly, and disposable fields are disposed.

**How a request flows.**

1. `ListenAsync` accepts connections on a pool thread and hands each to `ServeAsync`.
2. `ServeAsync` rejects anything that is not a POST to `/mcp`, and anything carrying a non-loopback `Origin` header.
   Browsers send `Origin`; command-line and SDK clients do not. That stops a web page you happen to have open from
   driving your engine.
3. It creates a transport and a server for that one request (`Stateless = true`), and lets the SDK handle the
   message. The SDK parses the JSON-RPC, finds the tool, and calls your delegate.
4. The delegate calls `OnMainThreadAsync`, which hands the real work to the main thread with
   `Threads.InvokeAsync(ThreadId.Main, …)` and returns its task.
5. The main thread runs it at the start of the next frame and completes the task.
6. The SDK writes the answer into the buffer and `ServeAsync` sends it.

**Why the answer is buffered.** A JSON-RPC notification (such as `notifications/initialized`) has no answer, and
Streamable HTTP wants a `202` for it. `HandlePostRequestAsync` only tells you whether it wrote anything after the
fact, and an HTTP status cannot change once the body has started. Buffering lets you decide afterwards.

**How tools are declared.** `McpServerTool.Create` turns a delegate into a tool: parameter names become the JSON
argument names, `[Description]` becomes their documentation (this is what an LLM reads), and a default value makes
an argument optional. Return a `string` for text, or a `ContentBlock` for anything else (`screenshot` returns an
image).

**Errors.** Throw `McpException` for anything the caller should read: the SDK turns it into a tool error carrying
your message (after "An error occurred invoking '<tool>': "). Any other exception type is reported as a generic
failure with no detail.

**Tools that skip the main thread.** `log_read` and `component_types` read what the gem keeps under its own lock,
and `asset_list` calls `Assets.FindAll`/`PathOf`, which are thread-safe. `screenshot` runs on the render thread,
which owns the GPU and the windows: `IRendering.Read` gives back what the main window last showed, and the
`Png` extension (Core's `PngExtensions`) encodes it in memory, so nothing is written to disk. Without a renderer or
a main window (`--headless`) it answers an error. Everything else touches the ECS, the world or the settings and
must be on the main thread.

**The log.** Implementing `ILogger` is all it takes: the gem loader (`Magic\Core\Internal\Gems.cs`) registers any
gem that is an `ILogger` with `Log`, and loads loggers as early as their dependencies allow. Lines logged before the
gem is constructed (the host's start-up, the gems loaded before it) went only to the loggers that existed then.

**Component types.** The gem is an `ITypeRegistrar<IComponent>`: the loader hands it every component struct of every
assembly already loaded and of each gem that loads later, and takes them back when a gem unloads, which is how
ChunkStreaming learns the names a chunk file uses. A name two types share is kept under the second one's full
name.

**Components by reflection.** `IEcs.Has<T>`, `TryGet<T>`, `Set<T>` and `Remove<T>` need a compile-time type. The
gem has a `Type` at runtime, so it closes each generic method with `MakeGenericMethod` and invokes it. `TryGet`'s
`out` value comes back in the arguments array. A failure inside such a call arrives wrapped in
`TargetInvocationException`, which `OnMainThreadAsync` unwraps so the caller sees the real message. ChunkStreaming
solves the same problem privately, with a generic column class closed over each type (`ComponentColumn<T>` in
`ChunkReader.cs`); if another caller ever appears, that is the moment to give `IEcs` untyped members instead.

**Settings.** `settings_get` writes every `[Settings]` class the `Settings` service holds (`Settings.All`) under
its name; `settings_set` hands one `Name.Property=value` line to `Settings.Set`, the same path the console's `set`
command takes, so the value lasts until the next preset and is never saved.

**`AssetJson.Compact`.** Components and settings go out as chunk files write them, on one line: the engine's one
JSON dialect, which already leaves out what a type only computes (`Transform`'s `Matrix`, which could not be set back).

### Checkpoint

```powershell
dotnet build Magic\Core\Core.csproj
```

No errors, and `Build\net10.0\Debug\bin\Gems\Mcp.dll` exists. Run the Samples project as usual:

```powershell
Build\net10.0\Debug\bin\Magic.exe --project Samples
```

The log shows `Loaded gem: Mcp v1.0.0 (static)` and no "MCP server listening" line, because the variable is not set.
The engine behaves exactly as before.

## Part 3: Talk to the engine

### 3.1 By hand

Terminal one, start the engine with the port set:

```powershell
$env:MAGIC_MCP_PORT = "47800"
Build\net10.0\Debug\bin\Magic.exe --project Samples
```

The log now includes `MCP server listening on http://localhost:47800/mcp`.

Terminal two, define a helper and call some tools:

```powershell
function Mcp($tool, $arguments = '{}') {
    $body = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"' + $tool + '","arguments":' + $arguments + '}}'
    $response = Invoke-WebRequest http://localhost:47800/mcp -Method Post -Body $body -ContentType "application/json" -UseBasicParsing
    [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
}

Mcp engine_info
Mcp command_list
Mcp entity_children
Mcp log_read '{"after":0,"max":5}'
```

Each prints an `event: message` line and a `data:` line holding the JSON-RPC answer, for example:

```
event: message
data: {"result":{"content":[{"type":"text","text":"["exit","get","portal","preset","restore","screenshot","set","summon"]"}]},"id":1,"jsonrpc":"2.0"}
```

The tool's own answer is the `text` value: a JSON string inside JSON, so its quotes arrive escaped as `"`.
That one is `["exit","get","portal","preset","restore","screenshot","set","summon"]`, from `command_list`.

Now change the world. `entity_children` with no parent answers what is under the world root: the `Hub` domain (and
`Loading`, the loading screen's domain). Take the id of `Hub` and walk down:

```powershell
Mcp entity_children '{"parent":"<id of Hub>"}'        # Globals, Chunks
Mcp entity_children '{"parent":"<id of Globals>"}'    # PostProcessing, Orbit, Camera, Moon, Floor, Question
Mcp entity_get '{"id":"<id of Moon>"}'
Mcp component_set '{"id":"<id of Moon>","component":"DirectionalLight","value":{"color":{"r":1,"g":0.2,"b":0.2},"intensity":1}}'
```

The Hub turns red in the engine window. Then:

```powershell
Mcp command_run '{"name":"nope"}'   # isError: "An error occurred invoking 'command_run': There is no console command called 'nope'."
Mcp quit
```

The engine exits. Clear the variable so later runs in this terminal are back to normal:

```powershell
Remove-Item Env:MAGIC_MCP_PORT
```

### 3.2 From Claude Code

Create `.mcp.json` in the repo root:

```json
{
  "mcpServers": {
    "magic": {
      "type": "http",
      "url": "http://localhost:47800/mcp"
    }
  }
}
```

Start the engine with `MAGIC_MCP_PORT=47800` as above, then start Claude Code in the repo. It asks once whether to
trust the project's MCP server. After that, ask it things like "what entities are in the open domain?" or "turn the
moon light red and take a screenshot". The server is only reachable while an engine is running on that port.

47800 is just the convention for "the engine I started by hand". Wizard picks a free port per run instead, so the
two never collide.

## Part 4: The Wizard server

Wizard is its own solution in `Wizard\` at the repo root, beside `Magic\`. Nothing in it is built by
`Magic-Wizard.slnx`, and nothing in it references Magic.

### 4.1 Cut Wizard off from the engine's build settings

The repo root's `Directory.Build.props` applies to every project beneath it: it would force the engine's output
paths, configurations, analyzers and banned APIs onto Wizard. MSBuild stops at the first `Directory.Build.props` it
finds walking up, so giving Wizard its own (which does not import the root's) cuts it off.

Create `Wizard\Directory.Build.props`:

```xml
<Project>

  <!-- Wizard is its own world: this file deliberately does not import the repo root's Directory.Build.props
       (MSBuild stops at the first one it finds walking up), so none of the engine's build settings apply here. -->

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <!-- Everything Wizard builds goes under the repo's ignored Build\ folder, beside the engine's output. -->
    <ArtifactsPath>$(MSBuildThisFileDirectory)..\Build\Wizard</ArtifactsPath>
  </PropertyGroup>

</Project>
```

With `ArtifactsPath`, the build lands in `Build\Wizard\bin\Wizard\debug\` and intermediates in `Build\Wizard\obj\`.
No `bin` or `obj` folders appear in the source tree.

The repo root's `.editorconfig` reaches Wizard the same way, and it makes the engine's rules errors; one of them
(CA2007, `ConfigureAwait(false)` on every `await`) is wrong for Blazor components, which must continue on their own
context. Create `Wizard\.editorconfig` to stop the walk there too:

```ini
# Wizard is its own world: the engine's code rules (the repo root's .editorconfig) do not apply here.
root = true
```

### 4.2 Scaffold the project and solution

```powershell
cd Wizard
dotnet new blazor --interactivity Server --all-interactive --empty --no-https -n Wizard -o Wizard
dotnet new sln --format slnx -n Wizard
dotnet sln Wizard.slnx add Wizard\Wizard.csproj
```

What the flags mean:

- `--interactivity Server`: components run in the server process and update the page over a websocket. That is
  what lets C# in a component start processes and hold an MCP client.
- `--all-interactive`: the whole app is interactive, not page by page.
- `--empty`: no sample pages or Bootstrap.
- `--no-https`: it only ever listens on localhost.

You now have `Wizard\Wizard.slnx` and the project in `Wizard\Wizard\`. Open
`Wizard\Wizard\Properties\launchSettings.json` and set `applicationUrl` to `http://localhost:5211` (the
template picks a random port; a fixed one makes the later steps copy-pasteable).

### 4.3 The project file

Replace `Wizard\Wizard\Wizard.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- TargetFramework, Nullable and ImplicitUsings come from Wizard\Directory.Build.props. -->
  <PropertyGroup>
    <BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>
  </PropertyGroup>

  <ItemGroup>
    <!-- The MCP client Wizard drives the engine with; the engine's Mcp gem uses the same package for the server side. -->
    <PackageReference Include="ModelContextProtocol.Core" Version="2.2.0" />
  </ItemGroup>

</Project>
```

### 4.4 Program.cs

Replace `Wizard\Wizard\Program.cs`:

```csharp
using Wizard.Components;
using Wizard.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One engine for the whole app: every panel talks to the same Magic process.
builder.Services.AddSingleton<Engine>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The desktop shell holds our stdin open. When it closes (the window was closed, or the shell died) we stop too,
// which disposes Engine and takes the Magic process down with us. A terminal run has no shell and skips this.
if (Environment.GetEnvironmentVariable("WIZARD_SHELL") == "1")
{
    _ = Task.Run(() =>
    {
        while (Console.In.ReadLine() is not null)
        {
        }

        app.Lifetime.StopApplication();
    });
}

app.Run();
```

Compared with the template this adds two things: the `Engine` singleton, and the stdin lifeline that Part 5 uses.
Because `Engine` is a singleton that implements `IAsyncDisposable`, the host disposes it on shutdown, and that is
what stops the engine process.

### 4.5 The Engine service

Create `Wizard\Wizard\Services\Engine.cs`. This is the only place in Wizard that knows how the engine is reached.

```csharp
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Wizard.Services;

/// <summary>
/// The Magic engine Wizard is driving: started as a child process on a project, then spoken to only through its
/// MCP server. Wizard references no engine assembly; a tool name and JSON are the whole contract, the same one an
/// LLM client gets. One instance for the app, so every panel sees the same engine.
/// </summary>
public sealed class Engine(IConfiguration configuration) : IAsyncDisposable
{
    private Process? _process;
    private McpClient? _client;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Raised when the engine starts or stops, on whatever thread noticed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The .magic file or project folder last started.
    /// </summary>
    public string Project { get; private set; } = "";

    public bool Running => _client is not null;

    /// <summary>
    /// Starts the engine on <paramref name="project"/>, replacing a running one. Throws with the reason when it cannot.
    /// </summary>
    public async Task StartAsync(string project)
    {
        await StopAsync();

        string executable = Executable();
        if (!File.Exists(executable))
            throw new EngineException($"Magic was not found at {executable}. Build Magic-Wizard.slnx first, or set Engine in appsettings.json.");

        int port = FreePort();
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // no console window; the log arrives through MCP
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.Environment["MAGIC_MCP_PORT"] = port.ToString();

        Process process = Process.Start(start) ?? throw new EngineException("Magic could not be started.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Stopped(process);

        // The gem starts listening a moment after the process does: keep knocking until it answers or the process gives up.
        McpClient? client = null;
        for (int attempt = 0; client is null; attempt++)
        {
            if (process.HasExited)
                throw new EngineException($"Magic exited with code {process.ExitCode} before its MCP server answered. Is the Mcp gem built?");

            if (attempt == 100)
            {
                process.Kill();
                throw new EngineException("Magic started but its MCP server never answered.");
            }

            try
            {
                client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(100);
            }
        }

        _process = process;
        _client = client;
        Project = project;
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks the engine to quit and waits for it; kills it if it does not.
    /// </summary>
    public async Task StopAsync()
    {
        if (_process is not { } process)
            return;

        try
        {
            using CancellationTokenSource patience = new(TimeSpan.FromSeconds(3));
            await CallAsync("quit");
            await process.WaitForExitAsync(patience.Token);
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException or InvalidOperationException)
        {
            if (!process.HasExited)
                process.Kill();
        }

        Stopped(process);
    }

    /// <summary>
    /// Calls an engine tool and answers its text. Throws <see cref="EngineException"/> with the engine's message when
    /// the tool fails or the engine is gone.
    /// </summary>
    public async Task<string> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        if (_client is not { } client)
            throw new EngineException("The engine is not running.");

        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(tool, arguments);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or McpException)
        {
            throw new EngineException($"The engine did not answer {tool}: {ex.Message}");
        }

        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (result.IsError == true)
            throw new EngineException(text);

        return text;
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void Stopped(Process process)
    {
        if (_process != process)
            return;

        _process = null;
        _client = null; // nothing to close: the server keeps no session
        Changed?.Invoke();
    }

    /// <summary>
    /// Where Magic is: the Engine setting if there is one, otherwise the Debug build of the repo this Wizard was built
    /// in, found by walking up to the folder that holds Magic-Wizard.slnx.
    /// </summary>
    private string Executable()
    {
        if (configuration["Engine"] is { Length: > 0 } configured)
            return configured;

        string name = OperatingSystem.IsWindows() ? "Magic.exe" : "Magic";
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Magic-Wizard.slnx")))
                return Path.Combine(folder.FullName, "Build", "net10.0", "Debug", "bin", name);
        }

        return name;
    }
}

/// <summary>
/// Something the engine said no to, or could not be asked. The message is fit to show.
/// </summary>
public sealed class EngineException(string message) : Exception(message);
```

Things worth knowing:

- **`Changed` fires on a non-UI thread** (the process's exit callback). Components must hop back with
  `InvokeAsync` before touching their state; the ones below do.
- **`EngineException` is the only exception a component has to catch.** `CallAsync` converts transport failures (the
  engine died mid-call) and tool errors (the engine said no) into it.
- **Finding the engine.** While Wizard is built inside this repo, walking up from its own output folder reaches
  `Magic-Wizard.slnx`, so it uses the Debug engine build with no configuration. To point elsewhere (an Optimize
  build, an installed engine), add `"Engine": "C:\\path\\to\\Magic.exe"` to `Wizard\Wizard\appsettings.json`.
- **Wizard uses `File` and `Process` directly.** The "disk access through `IFileSystem`" rule is the engine's;
  Wizard is a separate application and has no such service.

### 4.6 App.razor and imports

Replace `Wizard\Wizard\Components\App.razor`:

```razor
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <ResourcePreloader />
    <link rel="stylesheet" href="@Assets["app.css"]" />
    <link rel="stylesheet" href="@Assets["Wizard.styles.css"]" />
    <ImportMap />
    <HeadOutlet @rendermode="_interactive" />
</head>

<body>
    <Routes @rendermode="_interactive" />
    <ReconnectModal />
    <script>
        // The desktop shell's preload script defines window.wizard before this runs. In a plain browser tab there
        // is no shell, so the same calls exist but answer "nothing": the page works either way.
        window.wizard ??= { pickProject: async () => null };
    </script>
    <script src="@Assets["_framework/blazor.web.js"]"></script>
</body>

</html>

@code {
    // No prerendering: the page is only ever shown by one local window, and rendering twice would start every
    // panel's polling twice.
    private static readonly IComponentRenderMode _interactive = new InteractiveServerRenderMode(prerender: false);
}
```

Replace `Wizard\Wizard\Components\_Imports.razor`:

```razor
@using System.Net.Http
@using System.Net.Http.Json
@using System.Text.Json
@using System.Text.Json.Nodes
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using Wizard
@using Wizard.Components
@using Wizard.Components.Editor
@using Wizard.Components.Layout
@using Wizard.Services
```

### 4.7 The toolbar

Create the folder `Wizard\Wizard\Components\Editor` and in it `Toolbar.razor`:

```razor
@implements IDisposable
@inject Engine Engine
@inject IJSRuntime JS

<header class="toolbar">
    <span class="brand">Wizard</span>
    <input class="project" placeholder="Path to a .magic file or a project folder" @bind="_project" disabled="@Engine.Running" />
    <button @onclick="BrowseAsync" disabled="@Engine.Running">Browse…</button>
    @if (Engine.Running)
    {
        <button class="stop" @onclick="StopAsync">Stop</button>
    }
    else
    {
        <button class="play" @onclick="PlayAsync" disabled="@(_starting || _project.Length == 0)">Play</button>
    }
    <span class="status @(_failed ? "error" : "")">@_status</span>
</header>

@code {
    private readonly CancellationTokenSource _disposed = new();
    private string _project = "";
    private string _status = "Stopped";
    private bool _starting;
    private bool _failed;

    public void Dispose()
    {
        Engine.Changed -= EngineChanged;
        _disposed.Cancel();
    }

    protected override void OnInitialized()
    {
        _project = Engine.Project;
        Engine.Changed += EngineChanged;
        _ = PollAsync();
    }

    /// <summary>
    /// The shell's native file dialog; in a browser tab there is none and the path is typed instead.
    /// </summary>
    private async Task BrowseAsync()
    {
        string? picked = await JS.InvokeAsync<string?>("wizard.pickProject");
        if (picked is not null)
            _project = picked;
    }

    private async Task PlayAsync()
    {
        _starting = true;
        _failed = false;
        _status = "Starting…";
        try
        {
            await Engine.StartAsync(_project);
        }
        catch (EngineException ex)
        {
            _failed = true;
            _status = ex.Message;
        }

        _starting = false;
    }

    private async Task StopAsync()
    {
        await Engine.StopAsync();
    }

    private void EngineChanged()
    {
        _ = InvokeAsync(() =>
        {
            if (!Engine.Running && !_failed)
                _status = "Stopped";

            StateHasChanged();
        });
    }

    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
                if (!Engine.Running)
                    continue;

                try
                {
                    JsonNode info = JsonNode.Parse(await Engine.CallAsync("engine_info"))!;
                    _failed = false;
                    _status = $"{info["project"]} · frame {info["frame"]} · {info["entities"]} entities";
                }
                catch (EngineException)
                {
                    // it stopped between the check and the call; Changed says so
                }

                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

This is the pattern every panel follows:

- `OnInitialized` starts a `PollAsync` loop on a `PeriodicTimer`. The loop's continuations run on the component's own
  context, so it can change state and call `StateHasChanged` directly.
- `Dispose` cancels the loop.
- Engine failures are caught as `EngineException` and shown or ignored, never thrown out of the component (an
  unhandled exception would kill the page's connection).

### 4.8 The console

Create `Wizard\Wizard\Components\Editor\ConsolePanel.razor`. (Not `Console.razor`: that would shadow
`System.Console`.)

```razor
@implements IDisposable
@inject Engine Engine

<section class="panel console">
    <h2>Console</h2>
    <div class="lines">
        @* Shown bottom-up by CSS (column-reverse): the view stays pinned to the newest line without script. *@
        <div>
            @foreach (Line line in _lines)
            {
                <div class="line @line.Level.ToLowerInvariant()">@line.Message</div>
            }
        </div>
    </div>
    <input class="command" placeholder="Command (Enter to run)" @bind="_command" @bind:event="oninput" @onkeydown="KeyDownAsync" disabled="@(!Engine.Running)" />
</section>

@code {
    private const int Capacity = 1000;

    private readonly CancellationTokenSource _disposed = new();
    private readonly List<Line> _lines = [];
    private string _command = "";
    private long _lastId;

    public void Dispose()
    {
        Engine.Changed -= EngineChanged;
        _disposed.Cancel();
    }

    protected override void OnInitialized()
    {
        Engine.Changed += EngineChanged;
        _ = PollAsync();
    }

    private async Task KeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key != "Enter" || _command.Trim().Length == 0)
            return;

        string[] words = _command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Add(new Line(0, "Command", $"> {_command}"));
        _command = "";
        try
        {
            await Engine.CallAsync("command_run", new() { ["name"] = words[0], ["args"] = words[1..] });
        }
        catch (EngineException ex)
        {
            Add(new Line(0, "Error", ex.Message));
        }
    }

    private void EngineChanged()
    {
        // A new engine numbers its log from 1 again.
        _ = InvokeAsync(() =>
        {
            if (Engine.Running)
                _lastId = 0;

            StateHasChanged();
        });
    }

    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
                if (!Engine.Running)
                    continue;

                try
                {
                    string text = await Engine.CallAsync("log_read", new() { ["after"] = _lastId, ["max"] = 200 });
                    Line[] lines = JsonSerializer.Deserialize<Line[]>(text, JsonSerializerOptions.Web) ?? [];
                    foreach (Line line in lines)
                        Add(line);

                    if (lines.Length > 0)
                    {
                        _lastId = lines[^1].Id;
                        StateHasChanged();
                    }
                }
                catch (EngineException)
                {
                    // it stopped between the check and the call
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Add(Line line)
    {
        _lines.Add(line);
        if (_lines.Count > Capacity)
            _lines.RemoveRange(0, _lines.Count - Capacity);
    }

    private sealed record Line(long Id, string Level, string Message);
}
```

The console keeps its lines when the engine stops, so you can still read why it stopped. The level becomes a CSS
class (`warning`, `error`, …) that the stylesheet colours.

### 4.9 The page

Replace `Wizard\Wizard\Components\Pages\Home.razor`. Hierarchy and Inspector come in Part 6; for now the page has
the toolbar, an empty middle and the console.

```razor
@page "/"

<PageTitle>Wizard</PageTitle>

<div class="editor">
    <Toolbar />
    <section class="panel hierarchy"><h2>Hierarchy</h2></section>
    <section class="panel viewport">
        <p>The engine draws in its own window.</p>
    </section>
    <section class="panel inspector"><h2>Inspector</h2></section>
    <ConsolePanel />
</div>
```

### 4.10 The stylesheet

Replace `Wizard\Wizard\wwwroot\app.css`. It already contains the styles for the panels Part 6 adds.

```css
:root {
    color-scheme: dark;
    --surface: #1e1f22;
    --panel: #2b2d30;
    --raised: #393b40;
    --line: #43454a;
    --text: #dfe1e5;
    --muted: #9da0a8;
    --accent: #7c6cf2;
    --good: #4caf7a;
    --bad: #e5604d;
    --warn: #e0b341;
}

* {
    box-sizing: border-box;
}

html, body {
    height: 100%;
    margin: 0;
}

body {
    background: var(--surface);
    color: var(--text);
    font: 13px/1.4 system-ui, sans-serif;
    overflow: hidden;
}

input, select, button {
    background: var(--raised);
    border: 1px solid var(--line);
    border-radius: 4px;
    color: var(--text);
    font: inherit;
    padding: 3px 6px;
}

input:focus, select:focus, button:focus-visible {
    border-color: var(--accent);
    outline: none;
}

button {
    cursor: pointer;
}

button:disabled, input:disabled {
    cursor: default;
    opacity: 0.5;
}

/* The whole window: toolbar on top, three columns, console along the bottom. Docking replaces this grid later. */
.editor {
    display: grid;
    gap: 1px;
    grid-template-areas:
        "toolbar toolbar toolbar"
        "hierarchy viewport inspector"
        "console console console";
    grid-template-columns: 260px 1fr 340px;
    grid-template-rows: auto 1fr 220px;
    height: 100vh;
}

.toolbar {
    align-items: center;
    background: var(--panel);
    display: flex;
    gap: 8px;
    grid-area: toolbar;
    padding: 6px 10px;
}

.toolbar .brand {
    color: var(--accent);
    font-weight: 700;
}

.toolbar .project {
    flex: 0 1 460px;
}

.toolbar .play {
    background: var(--good);
    border-color: var(--good);
    color: #10231a;
}

.toolbar .stop {
    background: var(--bad);
    border-color: var(--bad);
    color: #2a0f0b;
}

.toolbar .status {
    color: var(--muted);
    margin-left: auto;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.error {
    color: var(--bad) !important;
}

.panel {
    background: var(--panel);
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
}

.panel h2 {
    align-items: center;
    border-bottom: 1px solid var(--line);
    color: var(--muted);
    display: flex;
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.06em;
    margin: 0;
    padding: 6px 10px;
    text-transform: uppercase;
}

.panel h2 .actions {
    display: flex;
    gap: 4px;
    margin-left: auto;
}

.panel h2 button {
    line-height: 1;
    padding: 2px 7px;
}

.hierarchy {
    grid-area: hierarchy;
}

.viewport {
    align-items: center;
    background: var(--surface);
    color: var(--muted);
    grid-area: viewport;
    justify-content: center;
}

.inspector {
    grid-area: inspector;
    overflow-y: auto;
}

.console {
    grid-area: console;
}

.tree {
    overflow: auto;
    padding: 4px 0;
}

.node .row {
    cursor: default;
    display: flex;
    gap: 2px;
    padding: 2px 8px;
    white-space: nowrap;
}

.node .row:hover {
    background: var(--raised);
}

.node .row.selected {
    background: var(--accent);
    color: white;
}

.node .row.disabled .name {
    opacity: 0.5;
}

.node .twisty {
    display: inline-block;
    width: 14px;
}

.node .children {
    padding-left: 14px;
}

.inspector .empty, .inspector .error {
    margin: 10px;
}

.inspector .empty {
    color: var(--muted);
}

.field {
    align-items: center;
    display: grid;
    gap: 8px;
    grid-template-columns: 96px 1fr;
    padding: 2px 10px;
}

.field span {
    color: var(--muted);
    overflow: hidden;
    text-overflow: ellipsis;
}

.field input:not([type=checkbox]) {
    min-width: 0;
    width: 100%;
}

.field input[type=checkbox] {
    justify-self: start;
}

.field input.number {
    font-variant-numeric: tabular-nums;
}

fieldset {
    border: 0;
    border-top: 1px solid var(--line);
    margin: 8px 0 0;
    padding: 4px 0;
}

fieldset.group {
    border-top: 0;
    margin: 0 0 0 10px;
    padding: 0;
}

fieldset.group > legend {
    color: var(--muted);
    padding: 2px 0;
}

fieldset.component > legend {
    font-weight: 600;
    padding: 0 10px;
}

fieldset.component > legend button {
    background: none;
    border: 0;
    color: var(--muted);
    padding: 0 4px;
}

.inspector .add {
    border-top: 1px solid var(--line);
    display: flex;
    gap: 6px;
    margin-top: 8px;
    padding: 8px 10px;
}

.inspector .add select {
    flex: 1;
}

/* column-reverse keeps the scroll position pinned to the bottom as lines arrive. */
.console .lines {
    display: flex;
    flex: 1;
    flex-direction: column-reverse;
    font: 12px/1.5 ui-monospace, Consolas, monospace;
    overflow-y: auto;
    padding: 4px 10px;
}

.console .line {
    white-space: pre-wrap;
    word-break: break-word;
}

.console .line.warning {
    color: var(--warn);
}

.console .line.error, .console .line.critical {
    color: var(--bad);
}

.console .line.debug, .console .line.verbose {
    color: var(--muted);
}

.console .line.command {
    color: var(--accent);
}

.console .command {
    border-radius: 0;
    border-width: 1px 0 0;
    font: 12px/1.5 ui-monospace, Consolas, monospace;
    padding: 6px 10px;
}

.blazor-error-boundary {
    background: #b32121;
    color: white;
    padding: 1rem;
}

.blazor-error-boundary::after {
    content: "An error has occurred.";
}
```

### Checkpoint

From `Wizard\`:

```powershell
dotnet build Wizard.slnx
dotnet run --project Wizard
```

It prints `Now listening on: http://localhost:5211`. Open that in a browser.

1. Paste the full path of a project into the box, for example that of `Samples` in your checkout
   (a folder or its `.magic` file both work), and press **Play**.
2. The engine window opens on the Hub. The status on the right shows `Samples · frame N · N entities` (a few
   hundred), counting up.
3. The console fills with the engine's log.
4. Type `screenshot` in the command box and press Enter: a `Screenshot: …png` line appears.
5. Type `nope`: a red line says there is no such command.
6. Press **Stop**: the engine window closes and the status says `Stopped`.

"Browse…" does nothing in a browser tab; it needs the shell from Part 5.

If Play reports that Magic exited before its MCP server answered, the engine build does not include the gem:
run `dotnet build Magic\Core\Core.csproj` from the repo root.

### Iterating with hot reload

For day-to-day UI work, use `dotnet watch` instead of `dotnet run`:

```powershell
dotnet watch --project Wizard
```

Edits to `.razor`, `.cs` and `.css` files apply to the open page without restarting. This is the fast loop; use
the Electron shell when you need to check the real window.

## Part 5: The Electron shell

The shell is a small Node project in `Wizard\Shell`. It is the only JavaScript in the repo, and it stays small: it
owns the window, the menu and native dialogs, and nothing about the editor itself.

### 5.1 package.json

Create `Wizard\Shell\package.json`:

```json
{
  "name": "wizard-shell",
  "productName": "Wizard",
  "version": "0.1.0",
  "description": "The desktop window around the Wizard editor.",
  "author": "Konfus",
  "license": "UNLICENSED",
  "private": true,
  "main": "main.js",
  "scripts": {
    "start": "electron .",
    "package": "electron-forge package",
    "make": "electron-forge make"
  }
}
```

Then, from `Wizard\Shell`:

```powershell
npm install --save-dev electron @electron-forge/cli @electron-forge/maker-zip
```

This adds a `devDependencies` section to `package.json`, writes `package-lock.json` (commit it), and creates
`node_modules` (never commit it; Part 9 ignores it). Electron Forge is the Electron project's own packaging tool;
you only need it in Part 8, but installing it now keeps this to one step.

Electron downloads its binary the first time it runs, not during `npm install`, so the first `npm start` takes a
little longer and prints `Downloading Electron binary...`.

### 5.2 main.js

Create `Wizard\Shell\main.js`:

```js
// The desktop shell: a native window around the Wizard web app, and nothing else. It starts the Wizard server
// (the C# Blazor app), waits for it to say where it is listening, and shows that address in a window. Everything
// the editor does lives in C#; this file only owns what a web page cannot do: the window, the menu, native dialogs.

const { app, BrowserWindow, Menu, dialog, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const path = require('node:path');

let server = null;
let window = null;

// Packaged, the published server sits in the app's resources. From source, it is the Debug build under Build\Wizard.
function serverPath() {
  const name = process.platform === 'win32' ? 'Wizard.exe' : 'Wizard';
  return app.isPackaged
    ? path.join(process.resourcesPath, 'wizard', name)
    : path.join(__dirname, '..', '..', 'Build', 'Wizard', 'bin', 'Wizard', 'debug', name);
}

// Resolves with the server's address. WIZARD_URL skips starting one: point the shell at a server you are already
// running under "dotnet watch" to get hot reload inside the real window.
function startServer() {
  if (process.env.WIZARD_URL)
    return Promise.resolve(process.env.WIZARD_URL);

  return new Promise((resolve, reject) => {
    const executable = serverPath();

    // Port 0: the system picks a free one and the server prints it. stdin stays open as a lifeline: the server
    // quits when it closes, which also happens if this process dies.
    server = spawn(executable, ['--urls', 'http://127.0.0.1:0'], {
      cwd: path.dirname(executable),
      env: { ...process.env, WIZARD_SHELL: '1', ASPNETCORE_ENVIRONMENT: app.isPackaged ? 'Production' : 'Development' },
      stdio: ['pipe', 'pipe', 'inherit'],
    });

    let output = '';
    let listening = false;
    server.stdout.on('data', chunk => {
      if (listening)
        return;

      output += chunk;
      const match = /Now listening on: (http:\/\/\S+)/.exec(output);
      if (match) {
        listening = true;
        resolve(match[1]);
      }
    });

    server.once('error', error => reject(new Error(`Could not start ${executable}: ${error.message}`)));
    server.once('exit', code => {
      server = null;
      reject(new Error(`${executable} exited with code ${code} before it was listening.`));
      app.quit(); // the editor is the server; without it there is nothing to show
    });
  });
}

function createMenu() {
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    ...(process.platform === 'darwin' ? [{ role: 'appMenu' }] : []),
    { label: 'File', submenu: [{ role: 'quit' }] },
    { role: 'editMenu' },
    { label: 'View', submenu: [{ role: 'reload' }, { role: 'toggleDevTools' }, { type: 'separator' }, { role: 'togglefullscreen' }] },
  ]));
}

async function createWindow() {
  const url = await startServer();

  window = new BrowserWindow({
    width: 1440,
    height: 900,
    title: 'Wizard',
    backgroundColor: '#1e1f22', // the page's own background, so there is no white flash before it loads
    show: false,
    webPreferences: { preload: path.join(__dirname, 'preload.js') },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' })); // the editor is one window; links do not spawn more
  window.once('ready-to-show', () => window.show());
  await window.loadURL(url);
}

// What the page can ask of the shell (see preload.js). Each answers with plain data.
ipcMain.handle('pick-project', async () => {
  const result = await dialog.showOpenDialog(window, {
    title: 'Open a Magic project',
    properties: ['openFile'],
    filters: [{ name: 'Magic project', extensions: ['magic'] }],
  });

  return result.canceled ? null : result.filePaths[0];
});

app.whenReady().then(() => {
  createMenu();
  return createWindow();
}).catch(error => {
  dialog.showErrorBox('Wizard could not start', error.message);
  app.quit();
});

app.on('window-all-closed', () => app.quit());

// Closing stdin tells the server to shut down cleanly, which stops the engine it started.
app.on('before-quit', () => {
  if (server)
    server.stdin.end();
});
```

How the pieces fit:

- **Finding the port.** The server is started with `--urls http://127.0.0.1:0`. Kestrel picks a free port and logs
  `Now listening on: http://127.0.0.1:NNNNN`; the shell reads that line from the server's stdout.
- **`ASPNETCORE_ENVIRONMENT`.** A build that has not been published only serves its static files (CSS, the Blazor
  script) in the Development environment. From source the shell runs the Debug build, so it sets Development;
  packaged, it runs published output and sets Production.
- **`cwd`.** ASP.NET Core uses the working directory as its content root, where it looks for `appsettings.json`.
  Setting it to the server's own folder makes that independent of where the shell was started.
- **The shutdown chain.** Window closed → `before-quit` closes the server's stdin → `Program.cs` sees end of input
  and stops the host → the host disposes `Engine` → `Engine.StopAsync` tells Magic to quit. Because it hangs on a pipe,
  the same chain runs if Electron crashes.

### 5.3 preload.js

Create `Wizard\Shell\preload.js`:

```js
// Runs inside the page before any of its scripts, with access to Electron the page itself never gets. It exposes
// one small object, window.wizard, whose functions forward to the shell (main.js). The Blazor side calls them
// through JS interop; in a plain browser tab App.razor supplies stand-ins that answer "nothing".

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('wizard', {
  // The path of a .magic file the user picked in the native open dialog, or null when cancelled.
  pickProject: () => ipcRenderer.invoke('pick-project'),
});
```

This is the whole pattern for any native feature you add later:

1. `main.js`: `ipcMain.handle('some-name', …)` does the native thing and returns plain data.
2. `preload.js`: add a function to `window.wizard` that invokes it.
3. `App.razor`: add a do-nothing stand-in to the fallback object so a browser tab still works.
4. A component: `await JS.InvokeAsync<T>("wizard.someName")`. `Toolbar.BrowseAsync` is the example.

### Checkpoint

From `Wizard\`, build the server, then start the shell:

```powershell
dotnet build Wizard.slnx
cd Shell
npm start
```

1. A native window titled Wizard opens, showing the same page as the browser did, with a File / Edit / View menu.
2. **Browse…** opens the native file dialog filtered to `.magic` files. Pick `Samples\Samples.magic`.
3. **Play** starts the engine, as before.
4. Close the Wizard window while the engine is running. Within a few seconds the engine window closes too. Check
   that nothing is left behind:

   ```powershell
   Get-Process Magic, Wizard, electron -ErrorAction SilentlyContinue
   ```

   It prints nothing.

**Hot reload inside the real window.** Run the server under `dotnet watch` in one terminal, and attach the shell to
it from another:

```powershell
# terminal one, in Wizard\
dotnet watch --project Wizard

# terminal two, in Wizard\Shell
$env:WIZARD_URL = "http://localhost:5211"
npm start
```

In this mode the shell does not start or stop the server; closing the window leaves `dotnet watch` running.
Remove the variable (`Remove-Item Env:WIZARD_URL`) to go back to normal.

View → Toggle Developer Tools gives you the browser devtools for layout and CSS work.

## Part 6: Hierarchy and Inspector

The engine tools already exist; this part is Wizard UI only.

### 6.1 The row type

Create `Wizard\Wizard\Services\EntityRow.cs`:

```csharp
using System.Text.Json;

namespace Wizard.Services;

/// <summary>
/// One line of the hierarchy, as the engine's <c>entity_children</c> tool answers it. The id is a string: it is 64-bit, and JSON numbers are not.
/// </summary>
public sealed record EntityRow(string Id, string? Name, bool Enabled, int Children)
{
    public static EntityRow[] Parse(string json)
    {
        return JsonSerializer.Deserialize<EntityRow[]>(json, JsonSerializerOptions.Web) ?? [];
    }
}
```

### 6.2 The hierarchy

Create `Wizard\Wizard\Components\Editor\Hierarchy.razor`:

```razor
@implements IDisposable
@inject Engine Engine

<section class="panel hierarchy">
    <h2>
        Hierarchy
        <span class="actions">
            <button title="Add an entity under the selected one" @onclick="CreateAsync" disabled="@(!Engine.Running || _roots.Length == 0)">+</button>
            <button title="Delete the selected entity" @onclick="DestroyAsync" disabled="@(!Engine.Running || Selected is null)">−</button>
        </span>
    </h2>
    <div class="tree">
        @foreach (EntityRow root in _roots)
        {
            <HierarchyNode @key="root.Id" Entity="root" Tick="_tick" Selected="@Selected" OnSelect="OnSelect" />
        }
    </div>
</section>

@code {
    private readonly CancellationTokenSource _disposed = new();
    private EntityRow[] _roots = [];
    private int _tick;

    public void Dispose()
    {
        _disposed.Cancel();
    }

    /// <summary>
    /// The selected entity's id, or null.
    /// </summary>
    [Parameter]
    public string? Selected { get; set; }

    [Parameter]
    public EventCallback<string?> OnSelect { get; set; }

    protected override void OnInitialized()
    {
        _ = PollAsync();
    }

    private async Task CreateAsync()
    {
        try
        {
            string id = await Engine.CallAsync("entity_create", new() { ["name"] = $"Entity{Random.Shared.Next(1000, 9999)}", ["parent"] = Selected ?? _roots[0].Id });
            await OnSelect.InvokeAsync(id);
        }
        catch (EngineException)
        {
            // the parent went away; the next refresh shows what is there
        }
    }

    private async Task DestroyAsync()
    {
        try
        {
            await Engine.CallAsync("entity_destroy", new() { ["id"] = Selected });
        }
        catch (EngineException)
        {
            // already gone
        }

        await OnSelect.InvokeAsync(null);
    }

    /// <summary>
    /// One timer for the whole tree: each tick reloads the roots here, and every open node reloads its own children when it sees the tick change.
    /// </summary>
    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
                try
                {
                    _roots = Engine.Running ? EntityRow.Parse(await Engine.CallAsync("entity_children")) : [];
                }
                catch (EngineException)
                {
                    _roots = [];
                }

                _tick++;
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

Create `Wizard\Wizard\Components\Editor\HierarchyNode.razor`. A node renders itself and, when open, a
`HierarchyNode` for each child.

```razor
@inject Engine Engine

<div class="node">
    <div class="row @(Selected == Entity.Id ? "selected" : "") @(Entity.Enabled ? "" : "disabled")" @onclick="() => OnSelect.InvokeAsync(Entity.Id)">
        <span class="twisty" @onclick="ToggleAsync" @onclick:stopPropagation="true">@(Entity.Children == 0 ? "" : _open ? "▾" : "▸")</span>
        <span class="name">@(Entity.Name ?? $"({Entity.Id})")</span>
    </div>
    @if (_open)
    {
        <div class="children">
            @foreach (EntityRow child in _children)
            {
                <HierarchyNode @key="child.Id" Entity="child" Tick="Tick" Selected="@Selected" OnSelect="OnSelect" />
            }
        </div>
    }
</div>

@code {
    private EntityRow[] _children = [];
    private bool _open;
    private int _loadedTick = -1;

    [Parameter, EditorRequired]
    public EntityRow Entity { get; set; } = null!;

    /// <summary>
    /// Counts the hierarchy's refreshes; an open node reloads its children when it changes.
    /// </summary>
    [Parameter]
    public int Tick { get; set; }

    [Parameter]
    public string? Selected { get; set; }

    [Parameter]
    public EventCallback<string?> OnSelect { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (!_open || _loadedTick == Tick)
            return;

        await LoadAsync();
    }

    private async Task ToggleAsync()
    {
        _open = !_open;
        if (_open)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadedTick = Tick;
        try
        {
            _children = EntityRow.Parse(await Engine.CallAsync("entity_children", new() { ["parent"] = Entity.Id }));
        }
        catch (EngineException)
        {
            _children = []; // this entity is gone; the parent's next refresh drops the node
        }
    }
}
```

Why it is shaped this way:

- **Lazy.** Only open nodes ask for their children, so a world with a hundred thousand entities costs the same
  as a small one until you open something.
- **One timer.** The `Hierarchy` owns the only timer. Each tick it bumps `Tick`, which flows down as a parameter;
  open nodes notice the change in `OnParametersSetAsync` and reload. No per-node timers to leak.
- **`@key`.** Keyed by entity id so Blazor keeps a node's open/closed state attached to the same entity when
  siblings come and go.

### 6.3 A field editor for any JSON value

Create `Wizard\Wizard\Components\Editor\JsonField.razor`. The inspector knows nothing about `Transform` or
`Camera`: it renders whatever JSON the engine sends. A new component type in a gem shows up in the inspector with
no Wizard change.

```razor
@using System.Globalization

@* One value of a component, edited in place in its owner. An object becomes a group of fields; a bool, number or
   string gets the matching input; anything else (an array, null) is edited as raw JSON. *@

@if (Owner[Key] is JsonObject group)
{
    <fieldset class="group">
        <legend>@Key</legend>
        @foreach (string key in group.Select(pair => pair.Key).ToArray())
        {
            <JsonField @key="key" Owner="group" Key="@key" Changed="Changed" />
        }
    </fieldset>
}
else
{
    <label class="field">
        <span>@Key</span>
        @switch (Owner[Key]?.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False:
                <input type="checkbox" checked="@(Owner[Key]!.GetValue<bool>())" @onchange="e => SetAsync(JsonValue.Create(e.Value is true))" />
                break;
            case JsonValueKind.Number:
                <input class="number" value="@(Owner[Key]!.ToJsonString())" @onchange="e => SetAsync(Number(e.Value?.ToString()))" />
                break;
            case JsonValueKind.String:
                <input value="@(Owner[Key]!.GetValue<string>())" @onchange="e => SetAsync(JsonValue.Create(e.Value?.ToString() ?? string.Empty))" />
                break;
            default:
                <input value="@(Owner[Key]?.ToJsonString() ?? "null")" @onchange="e => SetAsync(Json(e.Value?.ToString()))" />
                break;
        }
    </label>
}

@code {
    /// <summary>
    /// The object this value is a property of; an edit replaces the property in it.
    /// </summary>
    [Parameter, EditorRequired]
    public JsonObject Owner { get; set; } = null!;

    [Parameter, EditorRequired]
    public string Key { get; set; } = "";

    /// <summary>
    /// Raised after an edit changed <see cref="Owner"/>.
    /// </summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    /// <summary>
    /// Whole numbers stay whole: asset ids are 64-bit, and a trip through double would change them. Null (keep the old
    /// value) for text that is no number.
    /// </summary>
    private static JsonNode? Number(string? text)
    {
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong whole))
            return JsonValue.Create(whole);

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed))
            return JsonValue.Create(signed);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) ? JsonValue.Create(real) : null;
    }

    private static JsonNode? Json(string? text)
    {
        try
        {
            return JsonNode.Parse(text ?? "null");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SetAsync(JsonNode? value)
    {
        if (value is null)
            return;

        Owner[Key] = value;
        await Changed.InvokeAsync();
    }
}
```

Two Razor details that will bite you otherwise:

- `@(Owner[Key]!.GetValue<bool>())` needs the outer parentheses. Without them Razor reads `<bool>` as an HTML tag
  and the build fails with "method group cannot be made nullable".
- Number inputs are plain text inputs on purpose. A material id like `7381546209823741553` does not survive a
  browser's number input or a `double`.

### 6.4 The inspector

Create `Wizard\Wizard\Components\Editor\Inspector.razor`:

```razor
@implements IDisposable
@inject Engine Engine

<section class="panel inspector" @onfocusin="() => _editing = true" @onfocusout="() => _editing = false">
    <h2>Inspector</h2>
    @if (_entity is null)
    {
        <p class="empty">Select an entity.</p>
    }
    else
    {
        <label class="field">
            <span>name</span>
            <input value="@(_entity["name"]?.GetValue<string>())" @onchange="RenameAsync" />
        </label>
        <label class="field">
            <span>enabled</span>
            <input type="checkbox" checked="@(_entity["enabled"]!.GetValue<bool>())" @onchange="EnableAsync" />
        </label>

        @foreach ((string name, JsonNode? component) in _entity["components"]!.AsObject().ToArray())
        {
            JsonObject values = component!.AsObject();
            <fieldset class="component">
                <legend>
                    @name
                    <button title="Remove this component" @onclick="() => RemoveAsync(name)">×</button>
                </legend>
                @foreach (string key in values.Select(pair => pair.Key).ToArray())
                {
                    <JsonField @key="key" Owner="values" Key="@key" Changed="() => SetAsync(name, values)" />
                }
            </fieldset>
        }

        <div class="add">
            <select @bind="_adding">
                <option value="">Add component…</option>
                @foreach (string type in _types.Where(type => !_entity["components"]!.AsObject().ContainsKey(type)))
                {
                    <option value="@type">@type</option>
                }
            </select>
            <button @onclick="AddAsync" disabled="@(_adding.Length == 0)">Add</button>
        </div>

        @if (_error is not null)
        {
            <p class="error">@_error</p>
        }
    }
</section>

@code {
    private readonly CancellationTokenSource _disposed = new();
    private JsonObject? _entity;
    private string[] _types = [];
    private string _adding = "";
    private string? _error;
    private string? _loadedId;
    private bool _editing;

    public void Dispose()
    {
        _disposed.Cancel();
    }

    /// <summary>
    /// The entity to show, or null for none.
    /// </summary>
    [Parameter]
    public string? EntityId { get; set; }

    protected override void OnInitialized()
    {
        _ = PollAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (EntityId == _loadedId)
            return;

        _error = null;
        await LoadAsync();
    }

    private Task RenameAsync(ChangeEventArgs e)
    {
        return SendAsync("entity_rename", new() { ["id"] = EntityId, ["name"] = e.Value?.ToString() ?? "" });
    }

    private Task EnableAsync(ChangeEventArgs e)
    {
        return SendAsync("entity_enable", new() { ["id"] = EntityId, ["enabled"] = e.Value is true });
    }

    /// <summary>
    /// Sends the whole component: the engine replaces it, so what is on screen is what it becomes.
    /// </summary>
    private Task SetAsync(string component, JsonObject values)
    {
        return SendAsync("component_set", new() { ["id"] = EntityId, ["component"] = component, ["value"] = values.DeepClone() });
    }

    private Task RemoveAsync(string component)
    {
        return SendAsync("component_remove", new() { ["id"] = EntityId, ["component"] = component });
    }

    private async Task AddAsync()
    {
        await SendAsync("component_set", new() { ["id"] = EntityId, ["component"] = _adding, ["value"] = new JsonObject() });
        _adding = "";
    }

    /// <summary>
    /// Makes one edit, then shows what the engine now holds; a refusal is shown rather than thrown.
    /// </summary>
    private async Task SendAsync(string tool, Dictionary<string, object?> arguments)
    {
        try
        {
            await Engine.CallAsync(tool, arguments);
            _error = null;
        }
        catch (EngineException ex)
        {
            _error = ex.Message;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadedId = EntityId;
        if (EntityId is null || !Engine.Running)
        {
            _entity = null;
            return;
        }

        try
        {
            if (_types.Length == 0)
                _types = JsonSerializer.Deserialize<string[]>(await Engine.CallAsync("component_types")) ?? [];

            _entity = JsonNode.Parse(await Engine.CallAsync("entity_get", new() { ["id"] = EntityId }))!.AsObject();
        }
        catch (EngineException)
        {
            _entity = null; // the entity, or the engine, is gone
        }
    }

    /// <summary>
    /// Keeps the values live while the game changes them, except while a field has focus: typing must not be overwritten.
    /// </summary>
    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
                if (_editing || EntityId is null)
                    continue;

                await LoadAsync();
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

How an edit travels: you change a field → `JsonField.SetAsync` replaces the value inside the component's `JsonObject`
and raises `Changed` → the inspector sends the whole component with `component_set` → it reloads the entity, so the
screen shows what the engine actually holds, not what you typed. If the engine refuses (bad value), the message
appears in red and the reload puts the old value back.

The `_editing` flag matters: the Hub's camera orbits, so its `Transform` changes every frame. Without the flag, the
half-second refresh would overwrite a field while you are typing in it.

### 6.5 Put them on the page

Replace `Wizard\Wizard\Components\Pages\Home.razor`:

```razor
@page "/"

<PageTitle>Wizard</PageTitle>

<div class="editor">
    <Toolbar />
    <Hierarchy Selected="@_selected" OnSelect="@(id => _selected = id)" />
    <section class="panel viewport">
        <p>The engine draws in its own window.</p>
    </section>
    <Inspector EntityId="@_selected" />
    <ConsolePanel />
</div>

@code {
    private string? _selected;
}
```

The page owns the selection and hands it to both panels. That is the whole of Wizard's shared UI state for now.

### Checkpoint

Run Wizard (browser or shell), Play the Samples project, then:

1. The hierarchy shows `Loading` (the loading screen's domain) and `Hub`. Open `Hub`: `Globals` (PostProcessing,
   Orbit, Camera, Moon, Floor, Question) and `Chunks`, which is empty: the Hub is all globals.
2. Select `Moon`. The inspector shows `DirectionalLight` and `Transform`.
3. Change `intensity` to `1` and press Tab. The Hub brightens in the engine window, and the field still reads `1`
   after the next refresh.
4. Select `Camera`: its `Transform` values tick as the camera orbits. Click into a field: they hold still while it
   has focus.
5. With `Globals` selected press **+**. The new entity is selected (it appears under `Globals` once you open that
   node). Choose `Transform` under "Add component…" and press **Add**: a Transform with default values appears.
6. Press **−**: the entity is gone and the inspector empties.
7. Untick `enabled` on `Question`: it greys out in the tree on the next refresh.

Edits are live only. Stop and Play again and the world is back to what the chunk files say; saving is future work.

## Part 7: Tests

None in this pass, and that is the rule rather than a gap. The engine has unit tests only (`TESTING.md`): each
project's own, in a `Tests` folder beside it (`Magic\Gems\Mcp\Tests\Mcp.Tests.csproj` here), driving its public API
in memory, with no I/O and nothing made public for a test's sake. The Mcp gem makes nothing public: its class is
internal, and all it does is answer HTTP on a socket and call into other gems, which is I/O and more than one unit.
What a test could check is what Part 3 checks by hand.

Wizard has no tests either. Its logic is thin wiring over `Engine.CallAsync`.

## Part 8: Packaging

A packaged Wizard is the Electron app with the published Wizard server inside its resources.

### 8.1 Forge configuration

Create `Wizard\Shell\forge.config.js`:

```js
// Packaging only ("npm run package" / "npm run make"); "npm start" does not read this.
// The published Wizard server is copied into the app's resources as "wizard", which is where main.js looks for it.

const path = require('node:path');

module.exports = {
  packagerConfig: {
    name: 'Wizard',
    extraResource: [path.join(__dirname, '..', '..', 'Build', 'Wizard', 'shell', 'wizard')],
  },
  outDir: path.join(__dirname, '..', '..', 'Build', 'Wizard', 'shell', 'out'),
  makers: [
    { name: '@electron-forge/maker-zip' },
  ],
};
```

### 8.2 Publish, then package

From `Wizard\`:

```powershell
dotnet publish Wizard\Wizard.csproj -c Release -o ..\Build\Wizard\shell\wizard
cd Shell
npm run package
```

The app lands in `Build\Wizard\shell\out\Wizard-win32-x64\`. Run `Wizard.exe` there: it behaves like `npm start`,
but runs the published server in the Production environment.

To produce a zip you can hand to someone:

```powershell
npm run make
```

It writes `Build\Wizard\shell\out\make\zip\win32\x64\Wizard-win32-x64-0.1.0.zip` (about 150 MB; most of that is
Chromium).

Things to know before you distribute it:

- **The published server needs the .NET 10 ASP.NET runtime on the machine.** To remove that requirement, publish
  self-contained: add `-r win-x64 --self-contained` to the `dotnet publish` line (and the matching runtime
  identifier on other platforms).
- **It still has to find an engine.** Inside this repo the walk-up to `Magic-Wizard.slnx` works even from the
  packaged location. Anywhere else, set `"Engine"` in the `appsettings.json` beside the published `Wizard.exe`. How
  Wizard and Magic are distributed together is an open question for later.
- **One platform per build.** Electron Forge packages for the platform it runs on. A macOS or Linux build is made
  on that platform, with the server published for that platform.

## Part 9: Housekeeping

### 9.1 .gitignore

Add to the repo root `.gitignore`:

```gitignore
# Wizard's Electron shell: npm packages are restored by "npm install"
node_modules/
```

Everything else Wizard produces is already under `Build/`. Commit `Wizard\Shell\package-lock.json`.

### 9.2 README.md

In the root `README.md`, add Wizard to the folder structure, and point the introduction's link at the new README
instead of this tutorial:

```
Wizard/        The editor: its own solution (Wizard.slnx); see Wizard/README.md
```

And create `Wizard\README.md`:

````markdown
# Wizard

The Magic editor. It is a separate application: it starts `Magic.exe` on a project and drives it only through the
engine's MCP server (the `Mcp` gem), so everything Wizard can do, an LLM client can do too. Wizard references no
engine assembly.

```
Wizard/
  Wizard.slnx             The solution (not part of Magic-Wizard.slnx)
  Directory.Build.props   Wizard's own build settings; output goes to Build/Wizard/
  .editorconfig           Stops the engine's code rules at this folder
  Wizard/                 The editor: a Blazor Server app, all logic in C#
    Services/Engine.cs    Starts the engine and calls its MCP tools
    Components/Editor/    Toolbar, hierarchy, inspector, console
  Shell/                  The desktop window: Electron; owns the window, menu and native dialogs only
```

## Building and running

You need the .NET 10 SDK and Node. Build the engine first (`dotnet build Magic-Wizard.slnx` in the repo root).

```powershell
dotnet build Wizard.slnx
cd Shell
npm install
npm start
```

## Working on the UI

```powershell
dotnet watch --project Wizard      # then open http://localhost:5211 in a browser
```

To see the same hot-reloading page in the real window, set `WIZARD_URL` to that address before `npm start`.

## Adding a feature

1. If the engine cannot do it yet, add a tool to `Magic/Gems/Mcp/Mcp.cs`.
2. Call it with `Engine.CallAsync("tool_name", arguments)` from a component.
3. If it needs something only a desktop app can do (a native dialog, a menu item), add a handler in
   `Shell/main.js`, expose it in `Shell/preload.js`, and give it a stand-in in `Components/App.razor`.
````

Once that README exists, this tutorial has done its job and can be deleted.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `error CS0104: 'IComponent' is an ambiguous reference` in `Mcp.cs` | The `using IComponent = …;` alias line at the top of the file is missing. |
| Engine log shows `Skipping gem Mcp … nothing provides …` | A service the gem's constructor asks for is missing, for example running with a `gems` list in the `.magic` file that leaves out the ECS (`"-FlecsEcs"`). The gem needs `IEcs` (the FlecsEcs gem) and Core's services, `World`, `Assets` and `DebugUI` among them; the renderer and the windows are optional (without them `screenshot` answers an error). |
| `MissingMethodException` mentioning `Microsoft.Extensions.Logging` at engine start | Step 1.3 was skipped: two versions of the logging dll are fighting over one file. Add the pin and rebuild. |
| `HttpListenerException: Access is denied` at engine start | The prefix is not a localhost one. The gem only registers `localhost` and `127.0.0.1`, which need no admin rights; check for a typo. |
| `HttpListenerException` about the prefix being in use | Another engine is already listening on that port (usually a leftover from a manual run with 47800). Close it. |
| Wizard: "Magic was not found at …" | The engine is not built (`dotnet build Magic-Wizard.slnx`), or Wizard is running outside the repo and needs `"Engine"` in `appsettings.json`. |
| Wizard: "Magic exited with code 1 before its MCP server answered" | The project path is wrong or the project failed to load. Run the same `Magic.exe --project …` in a terminal to see why. |
| Wizard build fails with `CA2007` (`ConfigureAwait`) or other `CA` errors | The engine's `.editorconfig` reaches Wizard: `Wizard\.editorconfig` with `root = true` (step 4.1) is missing. |
| Wizard page loads unstyled when run from the Debug build | The server is running in Production without being published. Use `dotnet run`, or let the shell start it (it sets Development). |
| `error CS8978: 'method group' cannot be made nullable` in a `.razor` file | A generic call in an attribute is missing its outer parentheses: write `@(x.GetValue<bool>())`. |
| Shell: "Wizard could not start … ENOENT" | The server has not been built. Run `dotnet build Wizard.slnx` in `Wizard\`. |
| The engine keeps running after Wizard was killed from Task Manager | Killing the Wizard server skips its clean shutdown. Closing the window, or the shell crashing, does stop the engine; a hard kill of the server does not. Close the engine window by hand. |

## Where to go next

Roughly in the order they unlock each other:

1. **Saving.** Nothing writes `.chunk` or `.magic` files yet, so edits vanish on Stop. This needs an ECS-to-chunk
   serializer in the engine and a `domain_save` tool. It is the first thing that makes Wizard an editor rather than
   an inspector.
2. **Docking.** Replace the CSS grid in `app.css` with a docking layout. There is no big-backed docking component
   for Blazor, so this means vendoring a JavaScript library (dockview is the usual choice) behind one Razor wrapper
   component, with the layout saved per user.
3. **Native menu → editor actions.** File → Open Project and the like: `main.js` sends a message to the page,
   `preload.js` exposes a subscription, and a component invokes a C# method through a `DotNetObjectReference`.
4. **Asset browser.** `asset_list` exists; a panel over it, plus asset pickers in the inspector for `Handle<T>`
   fields (today they are raw ids).
5. **In-editor viewport.** Stream captured frames into the page and forward input back. `IRendering.Read` waits on
   the GPU for every capture, so this needs encoding and throttling work in the engine first.
6. **Undo.** Once saving exists: record each `component_set` with the value it replaced.
7. **Push instead of polling.** If the half-second polls ever show up in a profile, move the gem to a stateful MCP
   transport and send notifications. Polling is simpler and fine until then.
