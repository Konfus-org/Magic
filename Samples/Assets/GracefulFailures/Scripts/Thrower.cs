using Magic.Contexts;
using Magic.Interfaces;

namespace Samples.GracefulFailures;

/// <summary>
/// A script that throws in its first update: the scripting gem logs the exception and ends the script, and the
/// entity it was on stays in the world.
/// </summary>
internal sealed class Thrower : IBehavior
{
    public void Update(in Frame frame)
    {
        throw new InvalidOperationException("Thrower throws on purpose, to show a script failing.");
    }
}
