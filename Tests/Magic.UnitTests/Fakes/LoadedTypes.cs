using Magic.Interfaces;
using Magic.Services;
using System.Reflection;

namespace Magic.UnitTests.Fakes;

/// <summary>
/// What the host registers at start-up and as gems load, for a test: every assembly loaded in the test process that is
/// Core or references it (Core, the gems a test references, the test assemblies) handed to a registrar the way
/// <c>Gems</c> does.
/// </summary>
public static class LoadedTypes
{
    /// <summary>
    /// A <see cref="Types{T}"/> holding every loaded type of <typeparamref name="T"/>.
    /// </summary>
    public static Types<T> Of<T>()
    {
        return Into(new Types<T>());
    }

    /// <summary>
    /// <paramref name="registrar"/> (<see cref="Magic.Services.Assets"/>, a <see cref="Types{T}"/>) told about every loaded type it registers.
    /// </summary>
    public static TRegistrar Into<TRegistrar>(TRegistrar registrar) where TRegistrar : class
    {
        IServices container = new Container();
        container.Add(typeof(IRegisterFromGem), registrar);

        Assembly core = typeof(Types<>).Assembly;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly == core || assembly.GetReferencedAssemblies().Any(reference => reference.Name == core.GetName().Name))
                Gems.Register(container, Gems.TypesOf(assembly));
        }

        return registrar;
    }
}
