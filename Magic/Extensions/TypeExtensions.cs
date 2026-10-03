using Magic.Services;
using System.Reflection;

namespace Magic.Extensions;

/// <summary>
/// How the host constructs what it did not write: a gem, a script. The constructor's parameters are the
/// dependencies, each taken from the <see cref="Container"/> by its type.
/// </summary>
internal static class TypeExtensions
{
    extension(Type type)
    {
        /// <summary>
        /// The constructor instances are made with: the public one with the most parameters, or null.
        /// </summary>
        public ConstructorInfo? Constructor()
        {
            return type.GetConstructors().MaxBy(c => c.GetParameters().Length);
        }

        /// <summary>
        /// Constructs the type, each parameter taken from <paramref name="extras"/> (the first of that type) or else
        /// from <paramref name="container"/>. Throws <see cref="InvalidOperationException"/> when it has no public
        /// constructor or nothing provides a parameter; what the constructor itself throws comes out as it was.
        /// </summary>
        public object Create(Container container, params object[] extras)
        {
            ConstructorInfo constructor = type.Constructor()
                ?? throw new InvalidOperationException($"{type.FullName} has no public constructor.");

            ParameterInfo[] parameters = constructor.GetParameters();
            object[] arguments = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type wanted = parameters[i].ParameterType;
                arguments[i] = Array.Find(extras, wanted.IsInstanceOfType)
                    ?? (container.TryGet(wanted, out object? provided) ? provided : null)
                    ?? throw new InvalidOperationException($"{type.FullName} needs a {wanted.Name}, which nothing provides.");
            }

            try
            {
                return constructor.Invoke(arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
