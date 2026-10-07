using Magic.Services;
using Microsoft.Extensions.Logging;
using ZLogger;

#if RELEASE
using ZLogger.Providers;
#endif

using IGem = Magic.Interfaces.IGem;
using IMagicLogger = Magic.Interfaces.ILogger;
using MagicLogLevel = Magic.Interfaces.LogLevel;

namespace ZLoggerLoggingGem;

/// <summary>
/// The gem: every log line to the console through ZLogger, and from a Release build to a file as well.
/// </summary>
internal sealed class ZLoggerLogging : IGem, IMagicLogger
{
    private readonly ILoggerFactory _factory;
    private readonly ILogger _logger;

    public ZLoggerLogging(Project project)
    {
        _factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddZLoggerConsole();

#if RELEASE
            // Files only from a Release build, under Logs next to the executable, one name per project: one file a
            // day, rolled over to the next sequence number at 1 MB.
            logging.AddZLoggerRollingFile(options =>
            {
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    Path.Combine(Project.Logs, $"{project.Name}_{timestamp.ToLocalTime():yyyy-MM-dd}_{sequenceNumber:000}.log");
                options.RollingInterval = RollingInterval.Day;
                options.RollingSizeKB = 1024;
            });
#endif
        });

        _logger = _factory.CreateLogger(project.Name);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    public void Log(MagicLogLevel level, string message, string file, int line)
    {
        LogLevel zLevel = level switch
        {
            MagicLogLevel.Verbose => LogLevel.Trace,
            MagicLogLevel.Debug => LogLevel.Debug,
            MagicLogLevel.Information => LogLevel.Information,
            MagicLogLevel.Warning => LogLevel.Warning,
            MagicLogLevel.Error => LogLevel.Error,
            MagicLogLevel.Critical => LogLevel.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };

        _logger.ZLog(zLevel, $"{file}:{line} - {message}");
    }
}
