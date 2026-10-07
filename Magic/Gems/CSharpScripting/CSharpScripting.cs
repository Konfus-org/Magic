using Magic.Attributes.Scripts;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace CSharpScriptingGem;

/// <summary>
/// The gem: C# as a script language. A <c>.cs</c> <see cref="Script"/> asset loads as the class the project's build
/// compiled from it, an <see cref="IScript"/> named like the file (<see cref="ITypeRegistrar{T}"/> of scripts keeps every
/// one loaded); its <see cref="AlwaysUpdateAttribute"/> and <see cref="ImpatientAttribute"/> become the script's flags,
/// and an instance is constructed as a gem is (constructor parameters from the services, plus the entity's
/// <see cref="Handle"/>) and then its properties are set from its chunk entry (<see cref="AssetJson.Fill"/>). The
/// Scripting gem runs them.
/// </summary>
internal sealed class CSharpScripting : IGem, IAssetLoader<Script>, ITypeRegistrar<IScript>
{
    private readonly Types<IScript> _scripts = new();
    private readonly IServices _services;
    private readonly Subscription _gemsChanged;

    public CSharpScripting(Assets assets, Events events, IServices services)
    {
        _services = services;

        // A script asset holds a class of an assembly that may just have gone: every one is found again.
        _gemsChanged = events.Watch<GemsChanged>(_ => assets.Drop<Script>("gems changed"));
    }

    public void Dispose()
    {
        _gemsChanged.Dispose();
    }

    public Result Load(Script asset, byte[] bytes)
    {
        Type[] found = [.. _scripts.Named(asset.Name).Where(type => type.IsClass && type.Name == asset.Name)];
        if (found.Length == 0)
            return Result.Failure($"no loaded assembly has an IScript class named {asset.Name}; build the project.");
        if (found.Length > 1)
            return Result.Failure($"{found.Length} IScript classes are named {asset.Name} ({string.Join(", ", found.Select(type => type.Assembly.GetName().Name))}).");

        Type type = found[0];
        asset.AlwaysUpdate = type.IsDefined(typeof(AlwaysUpdateAttribute), inherit: true);
        asset.Impatient = type.IsDefined(typeof(ImpatientAttribute), inherit: true);
        asset.Make = (entity, entry) =>
        {
            // The instance is made through the host's injection, then the entry's values are set on it.
            IScript script = (IScript)type.Create(_services, entity);
            Result filled = AssetJson.Fill(script, entry);
            return filled.Ok ? script : throw new InvalidOperationException(filled.Message);
        };
        return Result.Success();
    }

    object? ITypeRegistrar.Register(Type type)
    {
        return ((ITypeRegistrar)_scripts).Register(type);
    }

    void ITypeRegistrar.Unregister(Type type)
    {
        ((ITypeRegistrar)_scripts).Unregister(type);
    }
}
