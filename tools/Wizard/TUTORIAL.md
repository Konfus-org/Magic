# Standing up Wizard

A step-by-step build of **Wizard**, the Magic editor, and of the gem that lets Wizard or an LLM drive the engine.
Work through it top to bottom, and run each part's checkpoint before going on.

Paths are relative to the repository root unless a step says otherwise. Commands are PowerShell and run under both
Windows PowerShell 5.1 and `pwsh`.

This guide is temporary: delete it once Part 8 has written `tools/Wizard/README.md`.

## What you are building

```
 Wizard desktop app (tools/Wizard)                   Magic (Build/bin/Debug)
 ┌──────────────────────────────────────┐            ┌─────────────────────────────────┐
 │ Electron shell  (Shell/)             │            │ Core: knows nothing of editors  │
 │   window, menu, native dialogs       │            │                                 │
 │        │ starts, shows its URL       │            │ Mcp gem (src/Bundled/Gems/Mcp)  │
 │        ▼                             │   starts   │   HttpListener on pool threads  │
 │ Wizard server  (Wizard/)             │───────────▶│   Threads ──▶ main thread       │
 │   Blazor Server, all logic in C#     │ --set port │   tools over World, Assets,     │
 │   Engine service ── MCP client ──────┼─── HTTP ──▶│   Assemblies, Settings, the log │
 └──────────────────────────────────────┘  localhost │                                 │
 Claude Code ── MCP over HTTP ──────────────────────▶│                                 │
                                                     └─────────────────────────────────┘
```

- **The engine speaks MCP.** A new bundled gem, `Mcp`, serves tools (`entity_list`, `component_set`, `log_read`, …)
  over localhost HTTP. Wizard and an LLM call the same tools. Wizard references no Magic assembly.
- **The shell shows Wizard's page.** The Wizard server is a Blazor Server app. Electron starts it and shows its address
  in a native window. The same address works in a browser tab.

When you're done, you can start the engine on a project with Play, read its log, run console commands, list and
filter entities, edit, add and remove components and entities, and do all of it from Claude Code too.

## Part 0: Prerequisites

```powershell
dotnet --version   # 10.0.100 or a later feature band of 10.0, as global.json says
node --version     # v24 or later; npm comes with it
```

Make sure the engine builds and passes its checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Validate.ps1
```

## Part 1: The Mcp gem

### 1.1 What Core gives the gem

The gem takes Core's public `World`, `Assemblies`, `Json`, `Settings`, `Threads` and `ICommands` in its constructor.
Core lacks one thing: a list of every component type, for an "add component" menu. Add it next to `Get<T>` in
`src/Core/Services/Assemblies.cs`:

```csharp
    /// <summary>
    /// Every top-level type in the code loaded that a file could name as a <typeparamref name="T"/>, each found again
    /// by its name with <see cref="Get{T}"/>: what a list to pick a component's struct from offers. It searches the
    /// code every time, so it is for a tool's menu, not the frame loop.
    /// </summary>
    public IReadOnlyList<Type> GetAll<T>()
    {
        List<Type> found = [];
        foreach (Assembly assembly in _code.All)
        {
            foreach (Type type in From(assembly))
            {
                if (!type.IsNested && !type.IsAbstract && !type.ContainsGenericParameters && type.IsAssignableTo(typeof(T)))
                {
                    found.Add(type);
                }
            }
        }

        return found;
    }
```

In `src/Core/README.md`, add this to "What a gem can ask for":

```markdown
`Assemblies` finds a type by its name, and lists every type of a kind for a tool to offer.
```

### 1.2 The project

Create `src/Bundled/Gems/Mcp/Mcp.csproj`. The rest of its build comes from `src/Bundled/Gems/Directory.Build.props`.

```xml
<!-- A gem: everything it needs to build comes from ../Directory.Build.props. -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Serves the engine to MCP clients, an editor or an LLM, over localhost HTTP.</Description>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <ItemGroup>
    <!-- The official C# MCP SDK's protocol and server, without ASP.NET Core, which a gem cannot load. -->
    <PackageReference Include="ModelContextProtocol.Core" />
  </ItemGroup>
</Project>
```

Add the SDK's version to `Directory.Packages.props`, in order:

```xml
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
    <PackageVersion Include="ModelContextProtocol.Core" Version="2.2.0" />
```

In `Magic.slnx`, add the project to the `/Gems/` folder, in order:

```xml
    <Project Path="src/Bundled/Gems/LogMetrics/LogMetrics.csproj" />
    <Project Path="src/Bundled/Gems/Mcp/Mcp.csproj" />
```

Nothing else registers it.

### 1.3 The settings

Create `src/Bundled/Gems/Mcp/McpSettings.cs`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Interfaces;

namespace Magic.Mcp;

/// <summary>
/// Where MCP clients reach the engine. Set with <c>--set Mcp.port=47800</c>, as an editor does for the engine it
/// starts, or in a preset.
/// </summary>
[Category("Debug")]
internal sealed class McpSettings : ISettings
{
    /// <summary>
    /// The localhost port MCP clients reach the engine on, at /mcp; 0 opens none, so a project can list the gem and
    /// only the runs that want a client open a port.
    /// </summary>
    [Range(0, 65535)]
    public int Port { get; set; }
}
```

F4 shows the doc comments, keeps the value in the range and lists the port under Debug.

### 1.4 The gem

Create `src/Bundled/Gems/Mcp/McpGem.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Logging;
using Magic.Contexts.Settings;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using IComponent = Magic.Interfaces.IComponent;
using Result = Magic.Contexts.Utils.Result;

namespace Magic.Mcp;

/// <summary>
/// Serves the engine to MCP clients, an editor or an LLM, over localhost HTTP, so they drive it without the engine
/// knowing who they are. Off until <see cref="McpSettings.Port"/> names a port, which it follows live. Requests arrive
/// on pool threads; whatever touches the world is handed to the main thread (<see cref="Threads"/>) and answers once
/// that has run it, at the start of the next frame. Stateless: one request, one answer, nothing pushed, so clients
/// poll (<c>log_read</c>). It keeps the latest log lines as a sink, from when it is made.
/// </summary>
internal sealed class McpGem : IGem, ILogSink, IDisposable
{
    private const int LogCapacity = 2000;

    private static readonly MethodInfo _has = typeof(World).GetMethod(nameof(World.Has))
        ?? throw new MissingMethodException(nameof(World), nameof(World.Has));

    private static readonly MethodInfo _remove = typeof(World).GetMethod(nameof(World.Remove))
        ?? throw new MissingMethodException(nameof(World), nameof(World.Remove));

    private readonly McpSettings _settings;
    private readonly World _world;
    private readonly Assets _assets;
    private readonly Assemblies _assemblies;
    private readonly Settings _gemSettings;
    private readonly Threads _threads;
    private readonly App _app;
    private readonly ICommands? _commands;
    private readonly McpServerOptions _options;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Guards the log: the log's own thread writes it while requests read it.
    /// </summary>
    private readonly Lock _lock = new();

    private readonly Queue<Line> _log = new();
    private long _lastLineId;
    private HttpListener? _listener;

    /// <summary>
    /// The port listened on, as the setting said when listening started, so a change is seen in the next frame.
    /// </summary>
    private int _port;

    /// <summary>
    /// The frame the main thread is in, for <c>engine_info</c>, which runs there.
    /// </summary>
    private ulong _frame;

    public McpGem(McpSettings settings, World world, Assets assets, Assemblies assemblies, Settings gemSettings, Threads threads, App app, ICommands? commands = null)
    {
        _settings = settings;
        _world = world;
        _assets = assets;
        _assemblies = assemblies;
        _gemSettings = gemSettings;
        _threads = threads;
        _app = app;
        _commands = commands;
        _options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Magic", Version = "1.0.0" },
            ServerInstructions = "Drives a running Magic engine. What touches the world answers once the engine's next frame has run it. Poll log_read for output.",
            ToolCollection =
            [
                CreateTool("engine_info", "The project, the frame the engine is on, how many entities there are, the open domains and whether the world has loaded.",
                    () => OnMainThreadAsync(FormatEngineInfo)),
                CreateTool("log_read", "Log lines newer than 'after', oldest first. Pass the last id seen to get only what is new.",
                    ([Description("Id of the last line already seen; 0 for every line kept.")] long after = 0,
                     [Description("Most lines to return.")] int max = 200) => FormatLog(after, max)),
                CreateTool("command_run", "Runs a console command as if typed at the terminal; 'help' lists them. What it says goes to the log: read it with log_read.",
                    ([Description("The command and its words, such as: help")] string line) => OnMainThreadAsync(() => RunCommand(line))),
                CreateTool("domain_open", "Starts opening a domain by its asset path; engine_info says once the world has loaded.",
                    ([Description("Path under the project's Assets folder or a Resources folder, with forward slashes, such as Domains/Valley/Valley.domain.")] string path,
                     [Description("True to open it beside the open domains; false to replace them.")] bool additive = false) => OpenDomain(path, additive)),
                CreateTool("domain_close", "Closes an open domain by its asset path, destroying what it spawned.",
                    ([Description("Path under the project's Assets folder or a Resources folder, with forward slashes.")] string path) => OnMainThreadAsync(() => CloseDomain(path))),
                CreateTool("entity_list", "Entities by id with the names of their components' types, those with a component of a type when one is given, a page at a time.",
                    ([Description("A component type's name, from component_types; empty for every entity.")] string component = "",
                     [Description("How many matching entities to skip.")] int skip = 0,
                     [Description("Most entities to return.")] int max = 100) => OnMainThreadAsync(() => FormatEntities(component, skip, max))),
                CreateTool("entity_get", "One entity's components, each by its type's name, as JSON in the engine's file format.",
                    ([Description("The entity's id, from entity_list.")] string id) => OnMainThreadAsync(() => FormatEntity(id))),
                CreateTool("entity_create", "Creates an entity with no components and answers its id.",
                    () => OnMainThreadAsync(() => FormatId(_world.Create()))),
                CreateTool("entity_destroy", "Destroys an entity and its components.",
                    ([Description("The entity's id.")] string id) => OnMainThreadAsync(() => DestroyEntity(id))),
                CreateTool("component_types", "The names of every component type an entity can have, in order.",
                    FormatComponentTypes),
                CreateTool("component_set", "Gives an entity a component, or overwrites the one it has. The value is the whole component, as entity_get shows it; fields left out take their defaults.",
                    ([Description("The entity's id.")] string id,
                     [Description("The component type's name, from component_types.")] string component,
                     [Description("The component as JSON, as entity_get shows it.")] JsonElement value) => SetComponentAsync(id, component, value)),
                CreateTool("component_remove", "Takes a component off an entity.",
                    ([Description("The entity's id.")] string id,
                     [Description("The component type's name.")] string component) => OnMainThreadAsync(() => RemoveComponent(id, component))),
                CreateTool("settings_get", "Every gem setting by its key (Gem.setting), with its value and what it is.",
                    FormatSettings),
                CreateTool("settings_set", "Changes a gem setting as the console's set does: live until a preset is applied, never saved.",
                    ([Description("The setting's key, from settings_get, such as VulkanGpu.hasVsync.")] string key,
                     [Description("The new value as JSON.")] JsonElement value) => SetSetting(key, value)),
                CreateTool("quit", "Ends the run once the current frame is done.",
                    Quit),
            ],
        };
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Close();
        _stopping.Dispose();
    }

    public void Update(in Frame frame)
    {
        _frame = frame.Index;
        if (_settings.Port != _port) Listen(_settings.Port);
    }

    public void Write(in LogLine line)
    {
        lock (_lock)
        {
            _log.Enqueue(new Line(++_lastLineId, line.Level, line.Message, line.Frame));
            if (_log.Count > LogCapacity) _log.Dequeue();
        }
    }

    private static McpServerTool CreateTool(string name, string description, Delegate run)
    {
        return McpServerTool.Create(run, new McpServerToolCreateOptions { Name = name, Description = description });
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Json.Options);
    }

    /// <summary>
    /// Ids travel as one string, the entity's three numbers, so a client hands back exactly what it was given.
    /// </summary>
    private static string FormatId(Entity entity)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{entity.Id}.{entity.EcsWorld}.{entity.Version}");
    }

    /// <summary>
    /// Serves on <paramref name="port"/> in place of the port served until now, or on none for 0. A port that cannot
    /// be had is an error, tried again once the setting changes.
    /// </summary>
    private void Listen(int port)
    {
        _port = port;
        _listener?.Close();
        _listener = null;
        if (port == 0) return;

        HttpListener listener = new();

        // Both names: the listener matches a request's Host header against its prefixes, and clients send either.
        listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/"));
        listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            Debugging.Error($"MCP clients cannot reach the engine on port {port}: {ex.Message}");
            return;
        }

        _listener = listener;
        AcceptAsync(listener, _stopping.Token).FireAndForget();
        Debugging.Info($"MCP clients reach the engine at http://localhost:{port}/mcp");
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                Debugging.Verbose($"MCP clients no longer reach the engine on this listener: {ex.Message}");
                return;
            }

            ServeAsync(context, cancel).FireAndForget();
        }
    }

    /// <summary>
    /// One POST is one JSON-RPC message, and its answer goes back as a server-sent event, as Streamable HTTP asks.
    /// </summary>
    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancel)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            // A web page could reach localhost too: only what no browser sent, or a page on this machine, gets through.
            if (request.Headers["Origin"] is { } origin && !(Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) && from.IsLoopback))
            {
                response.StatusCode = 403;
                return;
            }

            if (!string.Equals(request.Url?.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                return;
            }

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "POST";
                return;
            }

            await AnswerAsync(request, response, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or ObjectDisposedException or HttpListenerException or IOException)
        {
            Debugging.Verbose($"An MCP request ended unanswered: {ex.Message}");
            if (ex is JsonException) response.StatusCode = 400;
        }
        finally
        {
            Close(response);
        }
    }

    /// <summary>
    /// Has the SDK answer the request's message through a server made for it alone. The answer is buffered, as a
    /// notification has none and gets a 202, which is only known once the SDK is done, after which a status written
    /// with the body could not change.
    /// </summary>
    private async Task AnswerAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        JsonRpcMessage? message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(request.InputStream, McpJsonUtilities.DefaultOptions, cancel).ConfigureAwait(false);
        if (message is null)
        {
            response.StatusCode = 400;
            return;
        }

        StreamableHttpServerTransport transport = new() { Stateless = true };
        await using ConfiguredAsyncDisposable disposingTransport = transport.ConfigureAwait(false);
        McpServer server = McpServer.Create(transport, _options);
        await using ConfiguredAsyncDisposable disposingServer = server.ConfigureAwait(false);
        using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        server.RunAsync(answered.Token).FireAndForget();

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
        await body.CopyToAsync(response.OutputStream, cancel).ConfigureAwait(false);
    }

    private static void Close(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            Debugging.Verbose($"An MCP client went away before its answer: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread at the start of the next frame and hands back what it returned.
    /// What the world refuses, as the world throws it, becomes the tool's error, which the SDK passes on with its
    /// message; any other exception it reports with none.
    /// </summary>
    private Task<string> OnMainThreadAsync(Func<string> work)
    {
        return _threads.PostAsync(EngineThread.Main, () =>
        {
            try
            {
                return work();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                throw new McpException(ex.Message, ex);
            }
        }, _stopping.Token);
    }

    private string FormatEngineInfo()
    {
        int entities = 0;
        foreach (EcsChunk chunk in _world.Query())
        {
            entities += chunk.Entities.Length;
        }

        List<string?> domains = [];
        foreach (Handle<Domain> domain in _world.Opened)
        {
            domains.Add(_assets.GetPath(domain));
        }

        return Serialize(new
        {
            Project = _app.Project.Name,
            Root = _app.Folder,
            Frame = _frame,
            Entities = entities,
            Domains = domains,
            _world.IsLoading,
            _world.IsLoaded,
        });
    }

    private string FormatLog(long after, int max)
    {
        List<Line> lines = [];
        lock (_lock)
        {
            foreach (Line line in _log)
            {
                if (line.Id <= after) continue;
                if (lines.Count == max) break;

                lines.Add(line);
            }
        }

        return Serialize(lines);
    }

    private string RunCommand(string line)
    {
        if (_commands is not { } commands) throw new McpException("No gem the project lists provides console commands.");

        commands.Run(line);
        return "ran: what it said is in the log";
    }

    private string OpenDomain(string path, bool additive)
    {
        Handle<Domain> domain = FindDomain(path);
        OpenDomainAsync(domain, additive ? DomainOpenMode.Additive : DomainOpenMode.Replace, path).FireAndForget();
        return "opening";
    }

    private async Task OpenDomainAsync(Handle<Domain> domain, DomainOpenMode mode, string path)
    {
        Result<Domain> opened = await _world.OpenAsync(domain, mode, cancel: _stopping.Token).ConfigureAwait(false);
        if (!opened.Ok) Debugging.Error($"An MCP client's domain {path} did not open: {opened.Error}");
    }

    private string CloseDomain(string path)
    {
        _world.Close(FindDomain(path));
        return "closed";
    }

    private Handle<Domain> FindDomain(string path)
    {
        Handle<Domain> domain = _assets.Find<Domain>(path);
        return domain == Handle<Domain>.None ? throw new McpException($"There is no domain at {path}.") : domain;
    }

    /// <summary>
    /// The page of entities, gathered while the query runs, and described after it: reading an entity's components
    /// allocates, so it is done for the page alone.
    /// </summary>
    private string FormatEntities(string component, int skip, int max)
    {
        Func<Entity, bool>? has = component.Length == 0
            ? null
            : _has.MakeGenericMethod(FindComponentType(component)).CreateDelegate<Func<Entity, bool>>(_world);
        List<Entity> page = [];
        int total = 0;
        foreach (EcsChunk chunk in _world.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (has is not null && !has(entity)) continue;

                if (total >= skip && page.Count < max) page.Add(entity);

                total++;
            }
        }

        List<object> rows = new(page.Count);
        foreach (Entity entity in page)
        {
            IComponent[] components = _world.GetComponents(entity);
            string[] names = new string[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                names[i] = components[i].GetType().Name;
            }

            Array.Sort(names, StringComparer.Ordinal);
            rows.Add(new { Id = FormatId(entity), Components = names });
        }

        return Serialize(new { Total = total, Entities = rows });
    }

    private string FormatEntity(string id)
    {
        Entity entity = ParseEntity(id);
        JsonObject components = [];
        foreach (IComponent component in _world.GetComponents(entity))
        {
            Type type = component.GetType();
            Result<JsonElement> json = Json.Write(component, type);
            JsonNode? value = json.Ok ? JsonNode.Parse(json.Value.GetRawText()) : JsonValue.Create(json.Error);
            if (value is JsonObject fields) RemoveComputed(fields, type);

            components[type.Name] = value;
        }

        return new JsonObject { ["id"] = id, ["components"] = components }.ToJsonString();
    }

    /// <summary>
    /// Leaves out of <paramref name="json"/> what <paramref name="type"/> only computes, at every level, a transform's
    /// matrix, a rotation's isIdentity: the dialect writes it, but nothing sets it back, so a client is not offered it.
    /// </summary>
    private static void RemoveComputed(JsonObject json, Type type)
    {
        foreach (JsonPropertyInfo property in Json.Options.GetTypeInfo(type).Properties)
        {
            if (property.Set is null)
            {
                json.Remove(property.Name);
            }
            else if (json[property.Name] is JsonObject inner)
            {
                RemoveComputed(inner, property.PropertyType);
            }
        }
    }

    private string DestroyEntity(string id)
    {
        _world.Destroy(ParseEntity(id));
        return "destroyed";
    }

    private string FormatComponentTypes()
    {
        IReadOnlyList<Type> types = _assemblies.GetAll<IComponent>();
        string[] names = new string[types.Count];
        for (int i = 0; i < types.Count; i++)
        {
            names[i] = types[i].Name;
        }

        Array.Sort(names, StringComparer.Ordinal);
        return Serialize(names);
    }

    /// <summary>
    /// The value is read off the main thread, as reading touches nothing of the world; only setting it waits for a
    /// frame.
    /// </summary>
    private Task<string> SetComponentAsync(string id, string component, JsonElement value)
    {
        Type type = FindComponentType(component);
        Result<object> read = Json.Read(value, type);
        if (!read.Ok) throw new McpException($"That is no {type.Name}: {read.Error}");
        if (read.Value is not IComponent parsed) throw new McpException($"{type.Name} is no component.");

        return OnMainThreadAsync(() =>
        {
            _world.Set(ParseEntity(id), [parsed]);
            return "set";
        });
    }

    private string RemoveComponent(string id, string component)
    {
        Entity entity = ParseEntity(id);
        _remove.MakeGenericMethod(FindComponentType(component)).CreateDelegate<Action<Entity>>(_world)(entity);
        return "removed";
    }

    private string FormatSettings()
    {
        IReadOnlyList<Setting> all = _gemSettings.All;
        JsonArray settings = [];
        foreach (Setting setting in all)
        {
            Result<JsonElement> value = Json.Write(setting.Value, setting.Type);
            settings.Add(new JsonObject
            {
                ["key"] = setting.Key,
                ["value"] = value.Ok ? JsonNode.Parse(value.Value.GetRawText()) : null,
                ["description"] = setting.Description,
            });
        }

        return settings.ToJsonString();
    }

    private string SetSetting(string key, JsonElement value)
    {
        Result set = _gemSettings.TrySet(key, value);
        return set.Ok ? "set" : throw new McpException(set.Error);
    }

    private string Quit()
    {
        _app.Quit("an MCP client asked to quit");
        return "quitting";
    }

    private Type FindComponentType(string name)
    {
        Result<Type> type = _assemblies.TryGet<IComponent>(name);
        return type.Ok ? type.Value : throw new McpException($"{type.Error} component_types lists them.");
    }

    /// <summary>
    /// The live entity <paramref name="id"/> names, from <see cref="FormatId"/>.
    /// </summary>
    private Entity ParseEntity(string id)
    {
        string[] parts = id.Split('.');
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ecsWorld)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            Entity entity = new(slot, ecsWorld, version);
            if (_world.IsAlive(entity)) return entity;
        }

        throw new McpException($"There is no entity {id}; entity_list lists them.");
    }

    /// <summary>
    /// One log line as <c>log_read</c> answers it: numbered in the order logged, so a client asks for what is new.
    /// </summary>
    private readonly record struct Line(long Id, LogLevel Level, string Message, ulong? Frame);
}
```

### 1.5 Its README

Create `src/Bundled/Gems/Mcp/README.md`:

```markdown
# Mcp

Serves the running engine to MCP clients, such as an editor or an LLM, over localhost HTTP. A client references no
engine code: it asks the server which tools it has, and calls them with JSON.

## Using it

Set the gem's port setting to open a port, for example `--set Mcp.port=47800`. With no port set, it opens none. It
follows the setting while running. It listens on localhost only, and refuses requests a browser sends from a page on
another machine.

Each POST to `/mcp` carries one JSON-RPC message and gets one answer. Nothing is pushed: clients poll.

- Tools that touch the world run on the main thread at the start of the next frame. The others answer at once.
- `domain_open` starts opening a domain and answers at once. `engine_info` says when the world has loaded.
- An entity id is one string of three numbers, such as `58175.0.1`. Hand it back as you got it.
- `entity_list` returns a page of entities with their component types, and can filter by a component.
- A component travels as JSON under its type's name, without properties nothing can set, such as a transform's
  matrix. Setting a component replaces it whole: what you leave out takes its default.
- `log_read` returns the latest log lines, numbered, so you can ask for the lines after the last one you saw. It has
  only the lines logged after the gem started.
- Nothing is saved: changes last until the domain opens again. For a screenshot, run the console's `screenshot`
  command; the log says where it went.
```

### 1.6 List it

Add `Mcp` after `LogMetrics` in `samples/Samples.magic`:

```json
        "LogMetrics",
        "Mcp"
    ]
```

Do the same in `tools/Templates/Project/ProjectName.magic`, then run `tools/InstallTemplates.ps1` again to update the
installed template.

### Checkpoint

```powershell
dotnet build Magic.slnx
dotnet run --project samples
```

The build has no warnings or errors. The log's start-up lines include

```
Gem Mcp 1.0.0 by Konfus started: Serves the engine to MCP clients, an editor or an LLM, over localhost HTTP.
```

with no "MCP clients reach the engine" line after it: no port is set yet. Run `tools/Validate.ps1` again before you
commit the engine half.

## Part 2: Talk to the engine

### 2.1 By hand

In one terminal, run the samples with the port set:

```powershell
dotnet run --project samples -- --set Mcp.port=47800
```

Add `--headless` after the `--` to run without a window. The log now includes

```
MCP clients reach the engine at http://localhost:47800/mcp
```

In a second terminal, define a helper and call some tools:

```powershell
function Mcp($tool, $arguments = '{}') {
    $body = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"' + $tool + '","arguments":' + $arguments + '}}'
    $response = Invoke-WebRequest http://localhost:47800/mcp -Method Post -Body $body -ContentType "application/json" -UseBasicParsing
    [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
}

Mcp engine_info
Mcp component_types
Mcp log_read '{"after":0,"max":3}'
```

Each prints an `event: message` line and a `data:` line holding the JSON-RPC answer, for example:

```
event: message
data: {"result":{"content":[{"type":"text","text":"{"project":"Samples","root":"samples","frame":14297,"entities":49,"domains":["Domains/Hub/Hub.domain"],"isLoading":false,"isLoaded":true}"}]},"id":1,"jsonrpc":"2.0"}
```

The tool's answer is the `text` value: JSON inside a JSON string. `component_types` answers with the names of the
component types, such as `Camera`, `DirectionalLight` and `Transform`.

Now change the world. Find the sun, read it, and turn it red:

```powershell
Mcp entity_list '{"component":"DirectionalLight"}'   # two entities, the sun and the moon, such as 58175.0.1: DirectionalLight, Spin, Transform
Mcp entity_get '{"id":"58175.0.1"}'
Mcp component_set '{"id":"58175.0.1","component":"DirectionalLight","value":{"mode":"sun","color":{"r":1,"g":0.2,"b":0.2},"intensity":120000}}'
```

The world turns red in the engine window. Then try what it refuses:

```powershell
Mcp entity_get '{"id":"nope"}'                       # isError: "... There is no entity nope; entity_list lists them."
Mcp component_set '{"id":"58175.0.1","component":"DirectionalLight","value":{"intensty":5}}'
                                                     # isError: "... That is no DirectionalLight: The JSON property 'intensty' could not be mapped ..."
Mcp command_run '{"line":"nope"}'                    # ran; log_read then shows "There is no command nope; help lists them."
Mcp quit
```

The engine exits.

### 2.2 From Claude Code

Register the server with Claude Code:

```powershell
claude mcp add --transport http magic http://localhost:47800/mcp
```

Start the engine with `--set Mcp.port=47800`, then start Claude Code in the repository and ask it things like "which
entities have a light?" or "turn the sun red and take a screenshot". The server exists only while an engine runs on
that port.

Use 47800 for an engine you start by hand. Wizard picks a free port for each run.

## Part 3: The Wizard server

Wizard is its own solution in `tools/Wizard/`. `Magic.slnx` doesn't build it, and it references no Magic code.

### 3.1 Cut Wizard off from the engine's build

The repository's build settings, native-library targets, package versions and code rules must not reach Wizard. Give
it its own `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` and `.editorconfig`.

Create them before you scaffold the project in 3.2. Otherwise the first restore goes under
`Build/obj/Debug/Tools/Wizard/`; delete that folder if it did.

`tools/Wizard/Directory.Build.props`:

```xml
<Project>
  <!-- Wizard is an application of its own, not part of the engine. MSBuild stops at the first Directory.Build.props it
       finds walking up, and this one does not import the repository's, so none of the engine's build settings,
       analyzers or output folders apply here; Directory.Build.targets and Directory.Packages.props beside this file
       stop the other two walks the same way. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <!-- Everything Wizard builds goes under the repository's ignored Build/ folder, beside the engine's output. -->
    <ArtifactsPath>$(MSBuildThisFileDirectory)../../Build/Wizard</ArtifactsPath>
  </PropertyGroup>
</Project>
```

The build goes to `Build/Wizard/bin/Wizard/debug/`, and its intermediate files to `Build/Wizard/obj/`.

`tools/Wizard/Directory.Build.targets`:

```xml
<Project>
  <!-- Empty on purpose: it stops MSBuild's walk up to the repository's Directory.Build.targets, which builds the
       engine's native libraries. -->
</Project>
```

`tools/Wizard/Directory.Packages.props`:

```xml
<Project>
  <!-- Wizard's NuGet versions. A file of its own, so the repository's, with the analyzers it adds to every engine
       project, does not reach here. -->
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <!-- The same SDK the engine's Mcp gem serves with, for the client side. -->
    <PackageVersion Include="ModelContextProtocol.Core" Version="2.2.0" />
  </ItemGroup>
</Project>
```

`tools/Wizard/.editorconfig`:

```ini
# Wizard is an application of its own: the engine's code rules (the repository's .editorconfig) stop here. Blazor
# components continue on their own context after an await, so the engine's ConfigureAwait(false) rule is wrong here.
root = true
```

### 3.2 Scaffold the project and solution

```powershell
cd tools/Wizard
dotnet new blazor --interactivity Server --all-interactive --empty --no-https -n Wizard -o Wizard
dotnet new sln --format slnx -n Wizard
dotnet sln Wizard.slnx add Wizard/Wizard.csproj
```

This makes `tools/Wizard/Wizard.slnx` and a Blazor Server project in `tools/Wizard/Wizard/`, interactive throughout,
with no sample pages and no HTTPS. In `tools/Wizard/Wizard/Properties/launchSettings.json`, set `applicationUrl` to
`http://localhost:5211`.

### 3.3 The project file

Replace `tools/Wizard/Wizard/Wizard.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- TargetFramework, Nullable and ImplicitUsings come from tools/Wizard/Directory.Build.props. -->
  <PropertyGroup>
    <BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>
  </PropertyGroup>

  <ItemGroup>
    <!-- The MCP client Wizard drives the engine with; its version is in tools/Wizard/Directory.Packages.props. -->
    <PackageReference Include="ModelContextProtocol.Core" />
  </ItemGroup>

</Project>
```

### 3.4 Program.cs

Replace `tools/Wizard/Wizard/Program.cs`:

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

// The desktop shell holds stdin open. When it closes, because the window was closed or the shell died, Wizard stops
// too, which disposes Engine and so ends the Magic it started. A run from a terminal has no shell and skips this.
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

This adds the `Engine` singleton and the stdin lifeline Part 4 uses. The host disposes `Engine` on shutdown, which
stops the engine.

### 3.5 The Engine service

Create `tools/Wizard/Wizard/Services/Engine.cs`, the only part of Wizard that talks to the engine:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Wizard.Services;

/// <summary>
/// The Magic engine Wizard is driving: started as a child process on a project, then spoken to only through its MCP
/// server, the Mcp gem. Wizard references no engine assembly; a tool's name and its JSON are the whole contract, the
/// same one an LLM client gets. One for the app, so every panel sees the same engine.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    /// <summary>
    /// How long Magic may take to start before its MCP server answers: it loads every gem first.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private Process? _process;
    private McpClient? _client;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Raised when the engine starts or stops, on whichever thread noticed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The .magic file or project folder last started.
    /// </summary>
    public string Project { get; private set; } = "";

    public bool Running => _client is not null;

    /// <summary>
    /// Starts Magic on <paramref name="project"/> with its MCP server on a free port, in place of a running one.
    /// Throws with the reason when it cannot.
    /// </summary>
    public async Task StartAsync(string project)
    {
        await StopAsync();

        string executable = FindMagic();
        if (!File.Exists(executable))
            throw new EngineException($"Magic is not at {executable}. Build Magic.slnx, or set MagicDir to a Magic folder.");

        int port = FindFreePort();
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // Magic's log goes on to Wizard's own output (Forward), so why a run failed is there to read even before
            // the gem is made; its console gets an input of its own that nothing writes to.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--set");
        start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"Mcp.port={port}"));

        Process process = Process.Start(start) ?? throw new EngineException("Magic could not be started.");
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Stopped(process);
        _process = process;

        try
        {
            _client = await ConnectAsync(process, port);
        }
        catch (EngineException)
        {
            if (!process.HasExited)
                process.Kill();

            _process = null;
            throw;
        }

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
            using CancellationTokenSource patience = new(TimeSpan.FromSeconds(10));
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

    /// <summary>
    /// The gem starts listening in the engine's first frame, a moment after the process starts: keep knocking until it
    /// answers, the process gives up, or patience runs out.
    /// </summary>
    private static async Task<McpClient> ConnectAsync(Process process, int port)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited)
                throw new EngineException($"Magic exited with code {process.ExitCode} before its MCP server answered. Wizard's output has Magic's log.");

            if (waited.Elapsed > Patience)
                throw new EngineException("Magic started, but its MCP server never answered: does the project's .magic file list the Mcp gem? Wizard's output has Magic's log.");

            try
            {
                return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/mcp")),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(250);
            }
        }
    }

    private static int FindFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Magic's executable: the Debug build of the repository Wizard was built in, found by walking up to its
    /// Magic.slnx, or else the one in the Magic folder MagicDir names, as tools/InstallTemplates.ps1 sets it.
    /// </summary>
    private static string FindMagic()
    {
        string name = OperatingSystem.IsWindows() ? "Magic.exe" : "Magic";
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Magic.slnx")))
                return Path.Combine(folder.FullName, "Build", "bin", "Debug", name);
        }

        return Environment.GetEnvironmentVariable("MagicDir") is { Length: > 0 } magicDir ? Path.Combine(magicDir, name) : name;
    }

    private static void Forward(string? line)
    {
        if (line is not null)
            Console.WriteLine(line);
    }

    private void Stopped(Process process)
    {
        if (_process != process)
            return;

        _process = null;
        _client = null; // nothing to close: the server keeps no session
        Changed?.Invoke();
    }
}

/// <summary>
/// Something the engine said no to, or could not be asked. The message is fit to show.
/// </summary>
public sealed class EngineException(string message) : Exception(message);
```

Play starts Magic with `--set Mcp.port=<free port>`, and the engine's log shows in Wizard's terminal. Inside this
repository Wizard runs the Debug build next to `Magic.slnx`; elsewhere it runs the Magic in the folder `MagicDir` names.

### 3.6 App.razor and imports

Replace `tools/Wizard/Wizard/Components/App.razor`:

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
        // The desktop shell's preload script defines window.wizard before this runs. In a plain browser tab there is
        // no shell, so the same calls exist but answer "nothing": the page works either way.
        window.wizard ??= { pickProject: async () => null };
    </script>
    <script src="@Assets["_framework/blazor.web.js"]"></script>
</body>

</html>

@code {
    // No prerendering: the page is only ever shown by one local window, and rendering twice would start every panel's
    // polling twice.
    private static readonly IComponentRenderMode _interactive = new InteractiveServerRenderMode(prerender: false);
}
```

Replace `tools/Wizard/Wizard/Components/_Imports.razor`:

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

### 3.7 The toolbar

Create the folder `tools/Wizard/Wizard/Components/Editor` and in it `Toolbar.razor`:

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
    /// The shell's native file dialog; in a browser tab there is none, and the path is typed instead.
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
                    JsonNode? info = JsonNode.Parse(await Engine.CallAsync("engine_info"));
                    string loading = info?["isLoaded"]?.GetValue<bool>() == true ? "" : " · loading";
                    _failed = false;
                    _status = $"{info?["project"]} · frame {info?["frame"]} · {info?["entities"]} entities{loading}";
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

Every panel follows this pattern: `OnInitialized` starts a polling loop, `Dispose` cancels it, and engine failures are
caught as `EngineException`, never thrown out of the component.

### 3.8 The console

Create `tools/Wizard/Wizard/Components/Editor/ConsolePanel.razor` (not `Console.razor`, which would hide
`System.Console`):

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
                <div class="line @line.Level">@line.Message</div>
            }
        </div>
    </div>
    <input class="command" placeholder="Command (Enter to run; help lists them)" @bind="_command" @bind:event="oninput" @onkeydown="KeyDownAsync" disabled="@(!Engine.Running)" />
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

        string line = _command;
        Add(new Line(0, "command", $"> {line}"));
        _command = "";
        try
        {
            await Engine.CallAsync("command_run", new() { ["line"] = line });
        }
        catch (EngineException ex)
        {
            Add(new Line(0, "error", ex.Message));
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

    /// <summary>
    /// One log line as log_read answers it; the level (verbose, info, warn, error) is the line's CSS class.
    /// </summary>
    private sealed record Line(long Id, string Level, string Message);
}
```

The console keeps its lines when the engine stops. Each line's level is its CSS class.

### 3.9 The page

Replace `tools/Wizard/Wizard/Components/Pages/Home.razor`. Part 5 fills in the entity list and the inspector.

```razor
@page "/"

<PageTitle>Wizard</PageTitle>

<div class="editor">
    <Toolbar />
    <section class="panel entities"><h2>Entities</h2></section>
    <section class="panel viewport">
        <p>The engine draws in its own window.</p>
    </section>
    <section class="panel inspector"><h2>Inspector</h2></section>
    <ConsolePanel />
</div>
```

### 3.10 The stylesheet

Replace `tools/Wizard/Wizard/wwwroot/app.css`. It includes the styles for Part 5's panels.

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

button:disabled, input:disabled, select:disabled {
    cursor: default;
    opacity: 0.5;
}

/* The whole window: toolbar on top, three columns, console along the bottom. Docking replaces this grid later. */
.editor {
    display: grid;
    gap: 1px;
    grid-template-areas:
        "toolbar toolbar toolbar"
        "entities viewport inspector"
        "console console console";
    grid-template-columns: 300px 1fr 340px;
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
    gap: 6px;
    letter-spacing: 0.06em;
    margin: 0;
    padding: 6px 10px;
    text-transform: uppercase;
}

.panel h2 .count {
    font-weight: 400;
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

.entities {
    grid-area: entities;
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

.entities .filter {
    margin: 6px 10px;
}

.entities .list {
    overflow: auto;
    padding: 2px 0;
}

.entities .row {
    cursor: default;
    display: flex;
    gap: 8px;
    padding: 2px 10px;
    white-space: nowrap;
}

.entities .row:hover {
    background: var(--raised);
}

.entities .row.selected {
    background: var(--accent);
    color: white;
}

.entities .row .id {
    color: var(--muted);
    font-variant-numeric: tabular-nums;
}

.entities .row.selected .id {
    color: inherit;
}

.entities .row .components {
    overflow: hidden;
    text-overflow: ellipsis;
}

.entities .more {
    margin: 6px 10px;
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

.console .line.warn {
    color: var(--warn);
}

.console .line.error {
    color: var(--bad);
}

.console .line.verbose {
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

From `tools/Wizard/`:

```powershell
dotnet build Wizard.slnx
dotnet run --project Wizard
```

It prints `Now listening on: http://localhost:5211`. Open that in a browser.

1. Paste the full path of the samples folder, or of its `Samples.magic`, into the box and press **Play**.
2. The engine window opens on the Hub. The status on the right reads `Samples · frame N · 49 entities`, with the frame
   counting up.
3. The console fills with the engine's log, starting at `Gem Mcp 1.0.0 by Konfus started`. The terminal running
   Wizard shows the whole log.
4. Type `help` in the command box and press Enter: the engine's commands are listed.
5. Type `nope`: a yellow line says there is no such command.
6. Type `screenshot`: a `Screenshot: …png` line names the file under `Build/bin/Debug/Screenshots/`.
7. Press **Stop**: the engine window closes and the status reads `Stopped`.

**Browse…** does nothing in a browser tab; it needs the shell from Part 4.

### Iterating with hot reload

For day-to-day UI work, use `dotnet watch` instead of `dotnet run`:

```powershell
dotnet watch --project Wizard
```

Edits to `.razor`, `.cs` and `.css` files reach the open page without a restart.

## Part 4: The Electron shell

The shell is a small Node project in `tools/Wizard/Shell`. It owns the window, the menu and native dialogs, and
nothing else.

### 4.1 package.json

Create `tools/Wizard/Shell/package.json`:

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

Then, from `tools/Wizard/Shell`:

```powershell
npm install --save-dev electron@44 @electron-forge/cli@8 @electron-forge/maker-zip@8
```

This adds Electron and Electron Forge, its packaging tool, to `package.json` and writes `package-lock.json`. Commit
the lock file, never `node_modules`. The first `npm start` downloads the Electron binary.

### 4.2 main.js

Create `tools/Wizard/Shell/main.js`:

```js
// The desktop shell: a native window around the Wizard web app, and nothing else. It starts the Wizard server (the C#
// Blazor app), waits for it to say where it is listening, and shows that address in a window. Everything the editor
// does lives in C#; this file only owns what a web page cannot do: the window, the menu, native dialogs.

const { app, BrowserWindow, Menu, dialog, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const path = require('node:path');

let server = null;
let window = null;

// Packaged, the published server sits in the app's resources. From source, it is the Debug build under Build/Wizard.
function serverPath() {
  const name = process.platform === 'win32' ? 'Wizard.exe' : 'Wizard';
  return app.isPackaged
    ? path.join(process.resourcesPath, 'wizard', name)
    : path.join(__dirname, '..', '..', '..', 'Build', 'Wizard', 'bin', 'Wizard', 'debug', name);
}

// Resolves with the server's address. WIZARD_URL skips starting one: point the shell at a server already running under
// "dotnet watch" to get hot reload inside the real window.
function startServer() {
  if (process.env.WIZARD_URL)
    return Promise.resolve(process.env.WIZARD_URL);

  return new Promise((resolve, reject) => {
    const executable = serverPath();

    // Port 0: the system picks a free one and the server prints it. stdin stays open as a lifeline: the server quits
    // when it closes, which also happens if this process dies.
    server = spawn(executable, ['--urls', 'http://127.0.0.1:0'], {
      cwd: path.dirname(executable),
      env: { ...process.env, WIZARD_SHELL: '1', ASPNETCORE_ENVIRONMENT: app.isPackaged ? 'Production' : 'Development' },
      stdio: ['pipe', 'pipe', 'inherit'],
    });

    // Everything the server prints, the engine's log among it, goes on to the shell's own output.
    let output = '';
    let listening = false;
    server.stdout.on('data', chunk => {
      process.stdout.write(chunk);
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
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' })); // the editor is one window; links open no more
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

The shell starts the server on a free port, reads the address from its output and shows it. Closing the window, or
the shell crashing, closes the server's input, which stops the server and the engine.

### 4.3 preload.js

Create `tools/Wizard/Shell/preload.js`:

```js
// Runs inside the page before any of its scripts, with access to Electron the page itself never gets. It exposes one
// small object, window.wizard, whose functions forward to the shell (main.js). The Blazor side calls them through JS
// interop; in a plain browser tab App.razor supplies stand-ins that answer "nothing".

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('wizard', {
  // The path of a .magic file picked in the native open dialog, or null when cancelled.
  pickProject: () => ipcRenderer.invoke('pick-project'),
});
```

To add a native feature later:

1. `main.js`: `ipcMain.handle('some-name', …)` does the native thing and returns plain data.
2. `preload.js`: add a function to `window.wizard` that invokes it.
3. `App.razor`: add a do-nothing stand-in to the fallback object, for browser tabs.
4. A component: `await JS.InvokeAsync<T>("wizard.someName")`. `Toolbar.BrowseAsync` is the example.

### Checkpoint

From `tools/Wizard/`, build the server, then start the shell:

```powershell
dotnet build Wizard.slnx
cd Shell
npm start
```

1. A native window titled Wizard opens on the same page as the browser did, with a File, Edit and View menu.
2. **Browse…** opens the native file dialog, filtered to `.magic` files. Pick `samples/Samples.magic`.
3. **Play** starts the engine, as before; the terminal shows Wizard's and the engine's output.
4. Close the Wizard window while the engine runs. Within a few seconds the engine window closes too. Check that
   nothing is left behind:

   ```powershell
   Get-Process Magic, Wizard, electron -ErrorAction SilentlyContinue
   ```

   It prints nothing (unless another Magic of yours runs elsewhere).

**Hot reload in the real window.** Run the server under `dotnet watch` in one terminal, and point the shell at it
from another:

```powershell
# terminal one, in tools/Wizard
dotnet watch --project Wizard

# terminal two, in tools/Wizard/Shell
$env:WIZARD_URL = "http://localhost:5211"
npm start
```

The shell then neither starts nor stops the server. Remove the variable (`Remove-Item Env:WIZARD_URL`) to go back.

View → Toggle Developer Tools opens the browser's devtools.

## Part 5: Entities and Inspector

This part is Wizard's UI only. The engine's tools are already there.

### 5.1 The page type

Create `tools/Wizard/Wizard/Services/EntityPage.cs`:

```csharp
using System.Text.Json;

namespace Wizard.Services;

/// <summary>
/// A page of the world's entities, as the engine's <c>entity_list</c> tool answers it: how many match, and those on
/// the page.
/// </summary>
public sealed record EntityPage(int Total, EntityRow[] Entities)
{
    public static EntityPage None { get; } = new(0, []);

    public static EntityPage Parse(string json)
    {
        return JsonSerializer.Deserialize<EntityPage>(json, JsonSerializerOptions.Web) ?? None;
    }
}

/// <summary>
/// One entity on a page: its id, which the engine's tools take back as it is, and the names of its components' types,
/// which is what tells entities apart, as they have no names.
/// </summary>
public sealed record EntityRow(string Id, string[] Components);
```

### 5.2 The entity list

Create `tools/Wizard/Wizard/Components/Editor/EntityList.razor`:

```razor
@implements IDisposable
@inject Engine Engine

<section class="panel entities">
    <h2>
        Entities
        <span class="count">@_page.Total</span>
        <span class="actions">
            <button title="Add an entity" @onclick="CreateAsync" disabled="@(!Engine.Running)">+</button>
            <button title="Destroy the selected entity" @onclick="DestroyAsync" disabled="@(!Engine.Running || Selected is null)">−</button>
        </span>
    </h2>
    <select class="filter" @bind="_component" @bind:after="ReloadAsync" disabled="@(!Engine.Running)">
        <option value="">Every entity</option>
        @foreach (string type in _types)
        {
            <option value="@type">With @type</option>
        }
    </select>
    <div class="list">
        @foreach (EntityRow row in _page.Entities)
        {
            <div @key="row.Id" class="row @(row.Id == Selected ? "selected" : "")" @onclick="() => OnSelect.InvokeAsync(row.Id)">
                <span class="id">@row.Id</span>
                <span class="components">@string.Join(", ", row.Components)</span>
            </div>
        }
        @if (_page.Entities.Length < _page.Total)
        {
            <button class="more" @onclick="MoreAsync">Show @PageSize more</button>
        }
    </div>
</section>

@code {
    private const int PageSize = 100;

    private readonly CancellationTokenSource _disposed = new();
    private EntityPage _page = EntityPage.None;
    private string[] _types = [];
    private string _component = "";
    private int _shown = PageSize;

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
            await OnSelect.InvokeAsync(await Engine.CallAsync("entity_create"));
        }
        catch (EngineException)
        {
            // the engine stopped; the next refresh shows it
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
        await LoadAsync();
    }

    private async Task ReloadAsync()
    {
        _shown = PageSize;
        await LoadAsync();
    }

    private async Task MoreAsync()
    {
        _shown += PageSize;
        await LoadAsync();
    }

    /// <summary>
    /// The first entities that match the filter, as many as are shown: a world can hold far more than a list can, so
    /// only what is asked for is fetched.
    /// </summary>
    private async Task LoadAsync()
    {
        if (!Engine.Running)
        {
            _page = EntityPage.None;
            _types = [];
            return;
        }

        try
        {
            if (_types.Length == 0)
                _types = JsonSerializer.Deserialize<string[]>(await Engine.CallAsync("component_types")) ?? [];

            _page = EntityPage.Parse(await Engine.CallAsync("entity_list", new() { ["component"] = _component, ["max"] = _shown }));
        }
        catch (EngineException)
        {
            _page = EntityPage.None;
        }
    }

    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
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

The list shows the first hundred entities that match its filter, and **Show 100 more** asks for more. The header
counts every match. It polls once a second.

### 5.3 A field editor for any JSON value

Create `tools/Wizard/Wizard/Components/Editor/JsonField.razor`. It edits any JSON value the engine sends, whatever the
component type.

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
                <input type="checkbox" checked="@(Owner[Key]?.GetValue<bool>())" @onchange="e => SetAsync(JsonValue.Create(e.Value is true))" />
                break;
            case JsonValueKind.Number:
                <input class="number" value="@(Owner[Key]?.ToJsonString())" @onchange="e => SetAsync(Number(e.Value?.ToString()))" />
                break;
            case JsonValueKind.String:
                <input value="@(Owner[Key]?.GetValue<string>())" @onchange="e => SetAsync(JsonValue.Create(e.Value?.ToString() ?? string.Empty))" />
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
    public JsonObject Owner { get; set; } = [];

    [Parameter, EditorRequired]
    public string Key { get; set; } = "";

    /// <summary>
    /// Raised after an edit changed <see cref="Owner"/>.
    /// </summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    /// <summary>
    /// Whole numbers stay whole: asset ids are 64-bit, and a trip through double would change them. Null, keeping the
    /// old value, for text that is no number.
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

Keep the outer parentheses in `@(Owner[Key]?.GetValue<bool>())`; without them the build fails with CS8978. Numbers
use plain text inputs.

### 5.4 The inspector

Create `tools/Wizard/Wizard/Components/Editor/Inspector.razor`:

```razor
@implements IDisposable
@inject Engine Engine

<section class="panel inspector" @onfocusin="() => _editing = true" @onfocusout="() => _editing = false">
    <h2>Inspector <span class="count">@EntityId</span></h2>
    @if (_components is null)
    {
        <p class="empty">Select an entity.</p>
    }
    else
    {
        @foreach ((string name, JsonNode? component) in _components.ToArray())
        {
            <fieldset class="component">
                <legend>
                    @name
                    <button title="Remove this component" @onclick="() => RemoveAsync(name)">×</button>
                </legend>
                @if (component is JsonObject values)
                {
                    @foreach (string key in values.Select(pair => pair.Key).ToArray())
                    {
                        <JsonField @key="key" Owner="values" Key="@key" Changed="() => SetAsync(name)" />
                    }
                }
                else
                {
                    @* A component the engine writes as one value, such as the list Tags is, is edited as that value. *@
                    <JsonField Owner="_components" Key="@name" Changed="() => SetAsync(name)" />
                }
            </fieldset>
        }

        <div class="add">
            <select @bind="_adding">
                <option value="">Add component…</option>
                @foreach (string type in _types.Where(type => !_components.ContainsKey(type)))
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
    private JsonObject? _components;
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

    /// <summary>
    /// Sends the whole component: the engine replaces it, so what is on screen is what it becomes.
    /// </summary>
    private Task SetAsync(string component)
    {
        return SendAsync("component_set", new() { ["id"] = EntityId, ["component"] = component, ["value"] = _components?[component]?.DeepClone() });
    }

    private Task RemoveAsync(string component)
    {
        return SendAsync("component_remove", new() { ["id"] = EntityId, ["component"] = component });
    }

    /// <summary>
    /// An empty object: every field takes its default.
    /// </summary>
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
            _components = null;
            return;
        }

        try
        {
            if (_types.Length == 0)
                _types = JsonSerializer.Deserialize<string[]>(await Engine.CallAsync("component_types")) ?? [];

            _components = JsonNode.Parse(await Engine.CallAsync("entity_get", new() { ["id"] = EntityId }))?["components"]?.AsObject();
        }
        catch (EngineException)
        {
            _components = null; // the entity, or the engine, is gone
        }
    }

    /// <summary>
    /// Keeps the values live while the game changes them, except while a field has focus: typing must not be
    /// overwritten.
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

An edit sends the whole component with `component_set`, then reloads the entity. If the engine refuses a value, its
message shows in red and the old value comes back. A field you are typing in isn't refreshed until you leave it.

### 5.5 Put them on the page

Replace `tools/Wizard/Wizard/Components/Pages/Home.razor`:

```razor
@page "/"

<PageTitle>Wizard</PageTitle>

<div class="editor">
    <Toolbar />
    <EntityList Selected="@_selected" OnSelect="@(id => _selected = id)" />
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

The page owns the selection and hands it to both panels.

### Checkpoint

Run Wizard (in a browser or the shell), Play the samples, then:

1. Open the Valley (key 6). The entity list counts about 32,700, starting with the player's camera
   (`Camera, Scripts, Tags, Transform`) and the sun and the moon (`DirectionalLight, Spin, Transform`).
2. Choose **With Camera**: one entity. Choose **Every entity** again.
3. Select the sun. The inspector shows `Transform` (position, rotation and scale, no matrix), `DirectionalLight` and
   `Spin`.
4. Set the colour's `g` to `0.3` and `b` to `0.2`, pressing Tab after each. The world turns red in the engine window,
   and the fields still read `0.3` and `0.2` after the next refresh.
5. Press **+**. The new entity is selected, with no components. Choose `Transform` under "Add component…" and press
   **Add**: a Transform with its defaults appears (rotation `w` 1, scale 1), and the status counts one more entity.
6. Press **×** beside `Transform`: the entity has no components again. Press **−**: it is gone, and the count is back.

Edits aren't saved. Stop and Play again, and the world is back to what its prefabs say.

## Part 6: Tests

None. Part 2 checks the gem by hand. Add a Core.Tests test for `Assemblies.GetAll` only if one is asked for
([Tests](../../tests/README.md)).

## Part 7: Packaging

A packaged Wizard is the Electron app with the published Wizard server inside its resources.

### 7.1 Forge configuration

Create `tools/Wizard/Shell/forge.config.js`:

```js
// Packaging only ("npm run package", "npm run make"); "npm start" does not read this. The published Wizard server is
// copied into the app's resources as "wizard", which is where main.js looks for it once packaged.

const path = require('node:path');

const build = path.join(__dirname, '..', '..', '..', 'Build', 'Wizard', 'shell');

module.exports = {
  packagerConfig: {
    name: 'Wizard',
    extraResource: [path.join(build, 'wizard')],
  },
  outDir: path.join(build, 'out'),
  makers: [
    { name: '@electron-forge/maker-zip' },
  ],
};
```

### 7.2 Publish, then package

From `tools/Wizard/`:

```powershell
dotnet publish Wizard/Wizard.csproj -c Release -o ../../Build/Wizard/shell/wizard
cd Shell
npm run package
```

The app lands in `Build/Wizard/shell/out/Wizard-win32-x64/`. Run `Wizard.exe` there.

To make a zip you can hand to someone:

```powershell
npm run make
```

It writes `Build/Wizard/shell/out/make/zip/win32/x64/Wizard-win32-x64-0.1.0.zip`.

Before you hand it out:

- The machine needs the .NET 10 ASP.NET Core runtime, unless you publish self-contained: add
  `-r win-x64 --self-contained` to the `dotnet publish` line, or another platform's runtime identifier.
- Outside this repository, set `MagicDir` to a Magic folder.
- Electron Forge packages only for the platform it runs on.

## Part 8: Housekeeping

### 8.1 .gitignore

Add to the repository's `.gitignore`:

```gitignore
# Wizard's Electron shell: its npm packages are restored by "npm install"
node_modules/
```

Commit `tools/Wizard/Shell/package-lock.json`.

### 8.2 READMEs

Create `tools/Wizard/README.md`:

````markdown
# Wizard

The Magic editor. It starts Magic on a project and drives it through the MCP gem's tools, the same tools an LLM
client uses. Wizard references no engine code.

- **The server** is a Blazor Server app. Every panel is a C# component.
- **The shell** is an Electron app that owns the window, the menu and native dialogs. It starts the server on a free
  port and shows it. Closing the shell stops the server and the engine.

Wizard is its own solution, outside the engine's build and code rules. It builds into `Build/Wizard/`.

## Building and running

You need the .NET 10 SDK and Node. Build the engine first with `dotnet build Magic.slnx` at the repository root.

```powershell
dotnet build Wizard.slnx
cd Shell
npm install
npm start
```

Press Play to start Magic on a project, which must list the MCP gem. Wizard runs this repository's Debug build, or
else the Magic in the folder `MagicDir` names. The engine's log shows in Wizard's terminal.

## Working on the UI

Run `dotnet watch --project Wizard` and open the address it prints in a browser. To use the real window, set
`WIZARD_URL` to that address before `npm start`.

## Adding a feature

Add a tool to the MCP gem first, then a panel that calls it. For something only a desktop app can do, such as a
native dialog, add a handler in the shell, expose it in the shell's preload script, and give the page a stand-in for
browser tabs.
````

In `tools/README.md`, change the first sentence to:

```markdown
Scripts that build and check Magic, the repository's own analyzers, the templates for a new gem or project, and the
editor. Nothing here ships.
```

and add a section at its end:

```markdown
## Wizard

The Magic editor, with its own solution. It isn't part of a change's checks. See [its README](Wizard/README.md).
```

In the root `README.md`'s Folders block, make the `tools/` line:

```
tools/                     the repository's analyzers, the build and check scripts, and the editor (tools/Wizard); nothing here ships
```

Then delete this tutorial.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `CS0104: 'IComponent' is an ambiguous reference` or `'Result' is an ambiguous reference` in `McpGem.cs` | Add back the missing `using … = …;` alias at the top of the file. |
| The engine logs an error for `--set Mcp.port`, and Wizard says the MCP server never answered | Add `"Mcp"` to the project's `.magic` file (1.6). |
| `MCP clients cannot reach the engine on port N: …` | Another program holds the port, often an engine you started on 47800. Close it or pick another port. |
| Wizard: "Magic is not at …" | Build the engine (`dotnet build Magic.slnx`), or set `MagicDir` outside the repository. |
| Wizard: "Magic exited with code 1 before its MCP server answered" | Check the project's path. The engine's log in Wizard's terminal says what failed. |
| Wizard's build fails with `CA2007` or other engine rules, or restores the engine's analyzers | Add the missing file from 3.1: `.editorconfig` for the rules, `Directory.Packages.props` for the analyzers. |
| `Build/obj/Debug/Tools/Wizard/` appeared | Delete it. It came from scaffolding before 3.1's files existed. |
| The page loads unstyled from the Debug build | Use `dotnet run`, or let the shell start the server. |
| `CS8978: 'method group' cannot be made nullable` in a `.razor` file | Add the outer parentheses: `@(x.GetValue<bool>())`. |
| The shell: "Wizard could not start … ENOENT" | Build the server: `dotnet build Wizard.slnx` in `tools/Wizard/`. |
| A console command runs glued to the one before | Wait a moment after Enter before typing the next command. |
| The engine runs on after the Wizard server was killed | Close the engine window. Only a hard kill of the server skips stopping the engine. |

## Where to go next

In rough order:

1. **Saving.** Write `.prefab` files from the world, then add a `domain_save` tool.
2. **Names.** Keep a prefab part's name on the entity spawned from it, for a name column and, with parents, a tree.
3. **Docking.** Replace the CSS grid in `app.css` with a docking layout, saved per user.
4. **Native menu actions.** File → Open Project and the like, sent from `main.js` through `preload.js` to a component.
5. **Assets.** An `asset_list` tool over `Assets.GetPaths`, a panel for it, and pickers for `Handle<T>` fields.
6. **An in-editor viewport.** Stream a camera's render texture into the page, and send input back.
7. **A screenshot tool that returns the image.**
8. **Undo**, once saving exists: record each `component_set` with the value it replaced.
9. **Push instead of polling**, through a stateful MCP transport, if polling ever shows in a profile.
