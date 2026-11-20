using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Magic.Utils;

namespace Magic.CommandLine;

/// <summary>
/// The console commands that the gems of the run add to Magic's own, such as a subject of <c>query</c> that prints
/// what only the renderer knows.
/// </summary>
/// <remarks>
/// A gem takes this in its entry point's constructor and registers its command. The run has one of these, made with
/// the console, so a command lasts to the end of the run that added it.
/// </remarks>
public sealed class ConsoleCommands
{
    /// <summary>
    /// The commands registered and not yet given to the console.
    /// </summary>
    private readonly ConcurrentQueue<Command> _added = new();

    /// <summary>
    /// The subjects of <c>query</c> registered and not yet given to the console: the name, the description, and what
    /// reads the answer.
    /// </summary>
    private readonly ConcurrentQueue<(string Name, string Description, Func<JsonObject> Read)> _addedQueries = new();

    /// <summary>
    /// Each part that has a <see cref="CliPathAttribute"/>, by its path, with the type whose properties it shows.
    /// Game thread.
    /// </summary>
    private ImmutableDictionary<string, (object Service, Type Type)> _services = [];

    private readonly CommandQueue _queue;

    internal ConsoleCommands(CommandQueue queue) => _queue = queue;

    /// <summary>
    /// Adds <paramref name="command"/> to the console.
    /// </summary>
    /// <remarks>
    /// You can call it from any thread. Give the command a name that Magic's own commands do not have, or the console
    /// runs Magic's.
    /// </remarks>
    public void Register(Command command) => _added.Enqueue(command);

    /// <summary>
    /// Returns <paramref name="change"/>, a change to the world that a command started, which <c>wait loaded</c> waits
    /// for.
    /// </summary>
    /// <remarks>
    /// Call it on the game thread, in the action of the command, with the task that the change returned.
    /// </remarks>
    public Task<T> ChangeWorldAsync<T>(Task<T> change) => _queue.ChangeWorldAsync(change);

    /// <summary>
    /// Adds the subject <paramref name="name"/> to <c>query</c>: <c>query name</c> prints what
    /// <paramref name="read"/> gives, as JSON, or writes it to the file that <c>--out</c> names.
    /// </summary>
    /// <remarks>
    /// You can call it from any thread. <paramref name="read"/> runs on the game thread, at each query. Give the
    /// subject a name that Magic's own subjects do not have, or the console runs Magic's.
    /// </remarks>
    public void RegisterQuery(string name, string description, Func<JsonObject> read) =>
        _addedQueries.Enqueue((name, description, read));

    /// <summary>
    /// Gives <c>set</c> and <c>query</c> the properties of <paramref name="service"/> that <paramref name="type"/>
    /// has, under <paramref name="path"/>, until the subscription ends. Game thread.
    /// </summary>
    internal Subscription Register(string path, object service, Type type)
    {
        _services = _services.SetItem(path, (service, type));

        return new Subscription(() => _services = _services.Remove(path));
    }

    /// <summary>
    /// The part whose console path starts <paramref name="path"/>, with the type whose properties it shows; null when
    /// no part has that path. Game thread.
    /// </summary>
    internal (object Service, Type Type)? FindService(string path) =>
        _services.TryGetValue(path.Split('.')[0], out (object Service, Type Type) found) ? found : null;

    /// <summary>
    /// Takes the next command that a gem registered and the console does not hold yet; false when there is none.
    /// </summary>
    internal bool TryTake([NotNullWhen(true)] out Command? command) => _added.TryDequeue(out command);

    /// <summary>
    /// Takes the next subject of <c>query</c> that a gem registered and the console does not hold yet; false when
    /// there is none.
    /// </summary>
    internal bool TryTakeQuery(out (string Name, string Description, Func<JsonObject> Read) query) =>
        _addedQueries.TryDequeue(out query);
}
