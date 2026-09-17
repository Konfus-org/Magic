using DryIoc;

namespace Core.Services;

public static class GemServices
{
    /// <summary>
    /// Offers a service instance owned by the gem to the host and to other gems.
    /// The container only holds a weak reference, so the gem must keep the instance alive (a field) for as long
    /// as it is loaded, and dispose it in OnUnload. This is what lets the gem's assembly be collected on unload:
    /// a singleton the container created itself would be kept in its singleton scope until the container is disposed.
    /// </summary>
    public static void Provide<TService>(this IRegistrator services, TService instance, object? serviceKey = null)
        where TService : class
    {
        // asResolutionCall keeps consumers from inlining this instance into their own cached resolve expressions
        services.RegisterInstance(
            instance,
            setup: Setup.With(weaklyReferenced: true, asResolutionCall: true),
            serviceKey: serviceKey);
    }
}
