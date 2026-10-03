using Xunit;

namespace Magic.IntegrationTests;

/// <summary>
/// Tests that run a streaming system run one at a time: what it reports in <see cref="Magic.Utils.Debugging.Stats"/>
/// is process-wide.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StreamingCollection
{
    public const string Name = "Streaming";
}
