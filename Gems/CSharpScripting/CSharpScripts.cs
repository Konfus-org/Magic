using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace CSharpScriptingGem;

/// <summary>
/// Loads <see cref="Script"/> assets written in C#. It compiles nothing: the project's own build did, and the host
/// loaded the result. Loading a script finds its class, the one <see cref="IScript"/> named like the file
/// (<c>OrbitCamera.cs</c> declares <c>OrbitCamera</c>), among the script types loaded now (<see cref="Types{T}"/>). It keeps
/// none of them, so a project reloads freely under it.
/// </summary>
internal sealed class CSharpScripts(Types<IScript> scripts) : IGem, IAssetLoader<Script>
{
    public Result Load(Script asset, byte[] bytes)
    {
        Type[] found = [.. scripts.Named(asset.Name).Where(type => type.IsClass && type.Name == asset.Name)];
        if (found.Length == 0)
            return Result.Failure($"no loaded assembly has an IScript class named {asset.Name}; build the project.");

        if (found.Length > 1)
            return Result.Failure($"{found.Length} IScript classes are named {asset.Name} ({string.Join(", ", found.Select(type => type.FullName))}).");

        asset.Type = found[0];

        return Result.Success();
    }
}
