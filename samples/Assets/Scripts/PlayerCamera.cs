using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Where the player is, for the samples' scripts that answer the player: the first camera tagged
/// <see cref="Tag.Player"/>. There is none while a domain loads in place of the world, as the player comes with it.
/// </summary>
internal static class PlayerCamera
{
    public static bool TryGetPosition(World world, out Vector3 position)
    {
        foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in world.Query<Camera, Transform, Tags>())
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (!tags[i].Has(Tag.Player)) continue;

                position = transforms[i].Position;

                return true;
            }
        }

        position = default;

        return false;
    }
}
