using Magic.Attributes;
using Magic.Services;
using Microsoft.Extensions.Logging;
using ZLogger;
using IMagicLogger = Magic.Interfaces.ILogger;
using MagicLogLevel = Magic.Interfaces.LogLevel;

namespace ZLoggingGem;

[Gem(name: "ZLogging", version: "1.0.0", description: "Registers a logger implemented using ZLogger.", author: "Konfus", isStatic: true)]
[GemExport(typeof(IMagicLogger))]
internal sealed class ZLogger : IMagicLogger, IDisposable
{
    private readonly ILoggerFactory _factory;
    private readonly ILogger _logger;

    public ZLogger(Project project)
    {
        _factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);

            // Output Structured Logging, setup options
            logging.AddZLoggerConsole();

#if !DEBUG
            logging.AddZLoggerRollingFile(options =>
            {
                // File name determined by parameters to be rotated
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    $"{project.Logs}{timestamp.ToLocalTime():yyyy-MM-dd}_{sequenceNumber:000}.log";

                // The period of time for which you want to rotate files at time intervals.
                options.RollingInterval = RollingInterval.Day;

                // Limit of size if you want to rotate by file size. (KB)
                options.RollingSizeKB = 1024;
            });
#endif
        });

        _logger = _factory.CreateLogger(project.Name);
    }

    public void Flush()
    {
        // ZLogger writes from a background thread and exposes no flush; its providers drain their queues
        // only when the factory is disposed, which happens in Dispose when this gem is unloaded. A message
        // logged right before a hard crash can therefore still be lost.
    }

    public void Log(MagicLogLevel level, string message, string file, int line)
    {
        LogLevel logLvl = level switch
        {
            MagicLogLevel.Debug => LogLevel.Debug,
            MagicLogLevel.Information => LogLevel.Information,
            MagicLogLevel.Warning => LogLevel.Warning,
            MagicLogLevel.Error => LogLevel.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
        _logger.ZLog(logLvl, $"{file}:{line} - {message}");
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
