namespace Magic.Internal;

/// <summary>
/// Asking the container for what a gem may provide, such as a loader, a cache or a post-processor: no gem need provide
/// one, and the gem that does may not have been made.
/// </summary>
internal static class ServiceProviderExtensions
{
    extension(IServiceProvider services)
    {
        /// <summary>
        /// The <typeparamref name="T"/> a gem provides; null when none does, or when the gem that does was not made, which
        /// Core has said why and <paramref name="isMade"/> tells apart.
        /// </summary>
        internal T? GetProvided<T>(out bool isMade)
            where T : class
        {
            try
            {
                isMade = true;
                return services.GetService(typeof(T)) as T;
            }
            catch (InvalidOperationException)
            {
                isMade = false;
                return null;
            }
        }
    }
}
