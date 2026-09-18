using Magic.Attributes;
using Magic.Interfaces;
using TestGems.Contracts;

namespace LoggerGem;

/// <summary>Exports ILogger, so the host must load it first and unload it last.</summary>
[Gem("LoggerGem", "1.0.0", "Records log messages for the tests.")]
[GemExport]
public sealed class LoggerGem : ILogger, IDisposable
{
    public LoggerGem()
    {
        TestEvents.Events.Add("Logger.ctor");
    }

    public void Log(LogLevel level, string message, string file, int line)
    {
        TestEvents.Logs.Add($"{level}: {message}");
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
        TestEvents.Events.Add("Logger.dispose");
    }
}
