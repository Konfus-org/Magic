using Magic.Interfaces;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Extensions;

/// <summary>
/// <see cref="IServices"/> by type parameter instead of <see cref="Type"/>.
/// </summary>
public static class ServicesExtensions
{
    extension(IServices services)
    {
        /// <inheritdoc cref="IServices.Add"/>
        public void Add<T>(T instance) where T : class
        {
            services.Add(typeof(T), instance);
        }

        /// <inheritdoc cref="IServices.Remove"/>
        public void Remove<T>(T instance) where T : class
        {
            services.Remove(typeof(T), instance);
        }

        public bool Has<T>() where T : class
        {
            return services.Has(typeof(T));
        }

        /// <inheritdoc cref="IServices.Get"/>
        public T Get<T>() where T : class
        {
            return (T)services.Get(typeof(T));
        }

        /// <inheritdoc cref="IServices.TryGet"/>
        public bool TryGet<T>([NotNullWhen(true)] out T? instance) where T : class
        {
            bool found = services.TryGet(typeof(T), out object? untyped);
            instance = (T?)untyped;
            return found;
        }

        /// <inheritdoc cref="IServices.All"/>
        public T[] All<T>() where T : class
        {
            IReadOnlyList<object> all = services.All(typeof(T));
            T[] typed = new T[all.Count];
            for (int i = 0; i < typed.Length; i++)
                typed[i] = (T)all[i];

            return typed;
        }
    }
}
