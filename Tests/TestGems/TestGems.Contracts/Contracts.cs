namespace TestGems.Contracts;

public interface ITestService
{
    int Counter { get; set; }
}

public interface ITestConsumer
{
    ITestService Service { get; }
    byte[]? RestoredState { get; }
    object? OptionalImport { get; }
}

/// <summary>Process-wide record of what the test gems did, in order.</summary>
public static class TestEvents
{
    public static List<string> Events { get; } = [];

    /// <summary>Every message the test logger gem received, as "Level: message".</summary>
    public static List<string> Logs { get; } = [];

    public static void Reset()
    {
        Events.Clear();
        Logs.Clear();
    }
}
