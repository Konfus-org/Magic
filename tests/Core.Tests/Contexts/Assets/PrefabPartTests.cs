using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;

namespace Magic.Tests.Contexts.Assets;

public sealed class PrefabPartTests
{
    [Fact]
    public void PlaceUnder_puts_a_part_with_a_transform_under_its_parent()
    {
        PrefabPart part = new() { Components = [new Transform { Position = new Vector3D(1, 0, 0) }] };
        Transform parent = new() { Position = new Vector3D(10, 0, 0) };

        Transform placed = part.PlaceUnder(parent);

        Assert.Equal(new Vector3D(11, 0, 0), placed.Position);
    }

    [Fact]
    public void PlaceUnder_is_the_parents_place_for_a_part_without_a_transform()
    {
        PrefabPart part = new() { Components = [new Renderer()] };
        Transform parent = new() { Position = new Vector3D(10, 0, 0) };

        Transform placed = part.PlaceUnder(parent);

        Assert.Equal(parent, placed);
    }
}
