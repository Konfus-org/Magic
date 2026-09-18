using DryIoc;

namespace Magic.Utils;

public static class GemServices
{
    /// <summary>
    /// Offers a service instance owned by a gem to the host and to other gems.
    /// The container only holds a weak reference, so the owner must keep the instance alive for as long as
    /// the gem is loaded. This is what lets the gem's assembly be collected on unload: a singleton the
    /// container created itself would be kept in its singleton scope until the container is disposed.
    /// The gem loader calls this for every <see cref="Attributes.GemExportAttribute"/> contract and
    /// unregisters it again when the gem is unloaded.
    /// </summary>
    public static void Provide<TService>(this IRegistrator services, TService instance, object? serviceKey = null)
        where TService : class
    {
        services.ProvideAs(typeof(TService), instance, serviceKey);
    }

    /// <summary>
    /// <see cref="Provide{TService}"/> with the contract given as a <see cref="Type"/>. A separate name on
    /// purpose: as an overload of Provide, C# would bind Provide(typeof(X), instance) to the generic one
    /// with TService = Type and register the instance as a service key.
    /// </summary>
    public static void ProvideAs(this IRegistrator services, Type serviceType, object instance, object? serviceKey = null)
    {
        // Two gems exporting the same contract: the first one wins. Checked by hand because after an
        // Unregister (a gem unload) DryIoc still counts the removed entry as "already registered", so
        // IfAlreadyRegistered.Keep would silently drop the re-registration on a hot reload.
        if (services.IsRegistered(serviceType, serviceKey))
        {
            Log.Warn($"{serviceType} is already provided; keeping the existing one and ignoring {instance.GetType().FullName}.");
            return;
        }

        services.RegisterInstance(
            serviceType,
            instance,
            ifAlreadyRegistered: IfAlreadyRegistered.Replace,
            // asResolutionCall keeps consumers from inlining this instance into their own cached resolve expressions
            setup: Setup.With(weaklyReferenced: true, asResolutionCall: true),
            serviceKey: serviceKey);
    }
}
