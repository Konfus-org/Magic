using Xunit;

namespace Magic.IntegrationTests;

/// <summary>
/// Tests that initialise and quit the process-wide SDL run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SdlCollection
{
    public const string Name = "SDL";
}
