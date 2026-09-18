using Magic.Attributes;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;
using IMagicLogger = Magic.Interfaces.ILogger;
using MagicLogLevel = Magic.Interfaces.LogLevel;

namespace ZLoggingGem;

/// <summary>
/// The gem is the logger: exporting <see cref="IMagicLogger"/> makes the host load this gem before any
/// other, register it with <c>Magic.Services.Log</c>, and unload it last.
/// </summary>
[Gem("ZLogging", "1.0.0", "Registers a logger implemented using ZLogger.", Author = "Konfus")]
[GemExport]
internal sealed class ZLogger : IMagicLogger, IDisposable
{
    private readonly ILoggerFactory _factory;
    private readonly ILogger _logger;

    public ZLogger()
    {
        _factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);

            // Output Structured Logging, setup options
            logging.AddZLoggerConsole(options => options.UseJsonFormatter());
            logging.AddZLoggerRollingFile(options =>
            {
                // File name determined by parameters to be rotated
                options.FilePathSelector = (timestamp, sequenceNumber) => $"logs/{timestamp.ToLocalTime():yyyy-MM-dd}_{sequenceNumber:000}.log";

                // The period of time for which you want to rotate files at time intervals.
                options.RollingInterval = RollingInterval.Day;

                // Limit of size if you want to rotate by file size. (KB)
                options.RollingSizeKB = 1024;
                options.UseJsonFormatter();
            });
        });
        _logger = _factory.CreateLogger("Magic");
    }

    public void Flush()
    {
        // ZLogger does not require explicit flushing, but if we want to ensure all logs are written, we can implement a flush mechanism here if needed.
    }

    public void Log(MagicLogLevel level, string message, string file, int line)
    {
        LogLevel logLvl = level switch
        {
            MagicLogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            MagicLogLevel.Information => Microsoft.Extensions.Logging.LogLevel.Information,
            MagicLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            MagicLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };
        _logger.Log(logLvl, "{File}:{Line} - {Message}", file, line, message);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
