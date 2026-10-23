using Magic.Interfaces;
using Magic.Samples.Assets.Scripts;

namespace Magic.Samples.Assets.Components;

/// <summary>
/// When its entity goes, in the frame clock's seconds: <see cref="ExpireSystem"/> destroys it once that time has come.
/// <see cref="Spawner"/> gives what it makes one.
/// </summary>
internal record struct Expires : IComponent
{
    public double AtSec;
}
