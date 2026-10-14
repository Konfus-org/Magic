using Magic.Contexts.Logging;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// Somewhere log lines go besides the terminal: a file, an editor's console, a log service. A gem is one by
/// implementing it: <see cref="Debugging"/> sends it every line logged from when the gem is made until just before it
/// is disposed, which waits until it has had every line logged before, so its Dispose can close what it writes to.
/// Unlike the other interfaces a gem implements, it is not provided to other gems, so any number of gems can be sinks.
/// <para>
/// <see cref="Write"/> runs on the log's own background thread, one line at a time in the order lines were logged,
/// never on the main thread: a slow sink delays the log, never a frame. It is never called twice at once, but what it
/// shares with the gem's hooks, which run on the engine's threads meanwhile, needs a lock. A sink that throws is
/// dropped, and what it threw is logged as an error. A <see cref="LogLevel.Critical"/> line never reaches a sink: the
/// process ends first, and the crash file has it.
/// </para>
/// </summary>
public interface ILogSink
{
    void Write(in LogLine line);
}
