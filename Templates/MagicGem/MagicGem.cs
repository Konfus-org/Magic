using Core.Interfaces;
using DryIoc;

namespace MagicGem;

internal sealed class MagicGem : IGem
{
    public void OnLoad(IContainer services)
    {
        // Resolve host services here. To offer a service to the host and other gems, keep the
        // instance in a field and call services.Provide<IMyService>(_myService).
    }

    public void OnReloading(byte[] persist)
    {
        // Called before a hot reload: write any state to keep into `persist`.
    }

    public void OnReloaded(byte[] restore)
    {
        // Called after a hot reload: read the state written in OnReloading back from `restore`.
    }

    public void OnUnload()
    {
        // Dispose anything this gem owns, including services it provided.
    }
}
