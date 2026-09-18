using Magic.Interfaces;
using Magic.Utils;

namespace Core.Tests;

public sealed class LogTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Received { get; } = [];
        public void Log(LogLevel level, string message, string file, int line) => Received.Add((level, message));
        public void Flush() { }
    }

    [Fact]
    public void Messages_queued_before_a_logger_exists_keep_their_level()
    {
        // No logger is registered at this point (gem tests unload theirs), so these are queued.
        Log.Debug("queued debug");
        Log.Info("queued info");
        Log.Warn("queued warn");

        RecordingLogger logger = new();
        Log.Register(logger);
        try
        {
            Assert.Contains((LogLevel.Debug, "queued debug"), logger.Received);
            Assert.Contains((LogLevel.Information, "queued info"), logger.Received);
            Assert.Contains((LogLevel.Warning, "queued warn"), logger.Received);

            Log.Error("live error");
            Assert.Contains((LogLevel.Error, "live error"), logger.Received);
        }
        finally
        {
            Log.Unregister(logger);
        }
    }
}
