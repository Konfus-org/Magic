using Magic.Interfaces;
using System.Reflection;

namespace Magic.Extensions;

/// <summary>
/// How the host constructs what it did not write: a gem, a script. The constructor's parameters are the
/// dependencies, each taken from the <see cref="IServices"/> by its type: a <c>T</c> must be there, a <c>T?</c>
/// is null when it is not, and a <c>T[]</c> of a Core interface is every provider there is, in load order.
/// </summary>
public static class TypeExtensions
{
    private static readonly Assembly Host = typeof(TypeExtensions).Assembly;

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
        /// from <paramref name="services"/>. Throws <see cref="InvalidOperationException"/> when it has no public
        /// constructor or nothing provides a parameter that is not nullable; what the constructor itself throws
        /// comes out as it was.
        /// </summary>
        public object Create(IServices services, params object[] extras)
        {
            ConstructorInfo constructor = type.Constructor()
                ?? throw new InvalidOperationException($"{type.FullName} has no public constructor.");

            ParameterInfo[] parameters = constructor.GetParameters();
            object?[] arguments = new object?[parameters.Length];
            NullabilityInfoContext nullability = new();
            for (int i = 0; i < parameters.Length; i++)
            {
                Type wanted = parameters[i].ParameterType;
                object? argument = Array.Find(extras, wanted.IsInstanceOfType);
                if (argument is null && ManyOf(wanted) is { } element)
                    argument = ArrayOf(element, services.All(element));
                if (argument is null && services.TryGet(wanted, out object? provided))
                    argument = provided;
                if (argument is null && !IsOptional(nullability, parameters[i]))
                    throw new InvalidOperationException($"{type.FullName} needs a {wanted.Name}, which nothing provides.");

                arguments[i] = argument;
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

    /// <summary>
    /// The Core interface a <c>T[]</c> parameter asks every provider of, or null when the parameter is not one.
    /// </summary>
    public static Type? ManyOf(Type parameter)
    {
        return parameter.IsArray && parameter.GetElementType() is { IsInterface: true } element && element.Assembly == Host ? element : null;
    }

    /// <summary>
    /// Whether the parameter is declared nullable (<c>T?</c>): absent is null rather than an error.
    /// </summary>
    public static bool IsOptional(NullabilityInfoContext nullability, ParameterInfo parameter)
    {
        return !parameter.ParameterType.IsValueType && nullability.Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static Array ArrayOf(Type element, IReadOnlyList<object> instances)
    {
        Array array = Array.CreateInstance(element, instances.Count);
        for (int i = 0; i < instances.Count; i++)
            array.SetValue(instances[i], i);

        return array;
    }
}
