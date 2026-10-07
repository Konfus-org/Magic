namespace Magic.Interfaces;

/// <summary>
/// Everything a script took from a host service (an event watch, an ECS query) it disposes in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IScript : IDisposable
{
    /// <summary>
    /// Nothing to let go of by default; a script that took handles implements Dispose itself.
    /// </summary>
    void IDisposable.Dispose()
    {
    }
}
