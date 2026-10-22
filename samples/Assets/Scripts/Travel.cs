using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Jumps the player between the samples from the number keys: 0 opens the first of <see cref="Domains"/> in place of
/// the world, the hub, and 1 to 9 the ones after it. On the player, so it goes with it into every sample, and it takes
/// no entity, as it only reads the keys: <c>{ "id": 3431, "domains": [ { "id": 3400 }, { "id": 3406 } ] }</c>.
/// Nothing happens while a domain is loading.
/// </summary>
internal sealed class Travel(World world, IInput input) : IBehavior
{
    private static readonly Control[] _keys =
    [
        Control.Key0, Control.Key1, Control.Key2, Control.Key3, Control.Key4, Control.Key5, Control.Key6,
        Control.Key7, Control.Key8, Control.Key9,
    ];

    /// <summary>
    /// The domains the keys open, the hub first; one prefab's list, read by every copy of the script made from it.
    /// </summary>
    public IReadOnlyList<Handle<Domain>> Domains { get; set; } = [];

    public void Update(in Frame frame)
    {
        if (world.IsLoading) return;

        for (int i = 0; i < Math.Min(_keys.Length, Domains.Count); i++)
        {
            if (!input[_keys[i]].WasPressed) continue;

            Going.ToAsync(world, Domains[i], DomainOpenMode.Replace).FireAndForget();

            return;
        }
    }
}
