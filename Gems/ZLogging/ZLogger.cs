using IGem = Magic.Interfaces.IGem;
using Magic.Services;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;
using IMagicLogger = Magic.Interfaces.ILogger;
using MagicLogLevel = Magic.Interfaces.LogLevel;

namespace ZLoggingGem;

internal sealed class ZLogger : IGem, IMagicLogger
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

#if RELEASE
            // Files only from a Release build, under Logs next to the executable, one name per project.
            logging.AddZLoggerRollingFile(options =>
            {
                // File name determined by parameters to be rotated
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    Path.Combine(Project.Logs, $"{project.Name}_{timestamp.ToLocalTime():yyyy-MM-dd}_{sequenceNumber:000}.log");

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
            MagicLogLevel.Verbose => LogLevel.Trace,
            MagicLogLevel.Debug => LogLevel.Debug,
            MagicLogLevel.Information => LogLevel.Information,
            MagicLogLevel.Warning => LogLevel.Warning,
            MagicLogLevel.Error => LogLevel.Error,
            MagicLogLevel.Critical => LogLevel.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };

        _logger.ZLog(logLvl, $"{file}:{line} - {message}");
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
