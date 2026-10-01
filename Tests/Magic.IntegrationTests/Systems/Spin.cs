using Magic.Contexts.Components;

namespace Magic.IntegrationTests.Systems;

/// <summary>A component declared outside Core: a chunk names it and it loads with no registration.</summary>
public struct Spin : IComponent
{
    public float Speed { get; set; }
}
