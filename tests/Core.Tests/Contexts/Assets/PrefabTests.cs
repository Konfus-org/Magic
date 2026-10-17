using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;

namespace Magic.Tests.Contexts.Assets;

public sealed class PrefabTests
{
    [Fact]
    public void PlaceOf_finds_a_tagged_top_part()
    {
        Tag spawn = Tag.Of("spawn");
        Prefab prefab = new()
        {
            Parts = [new PrefabPart { Components = [At(1, 2, 3), Tagged(spawn)] }],
        };

        Transform? place = prefab.PlaceOf(spawn);

        Assert.Equal<Vector3D?>(new Vector3D(1, 2, 3), place?.Position);
    }

    [Fact]
    public void PlaceOf_places_a_tagged_child_under_its_parent()
    {
        Tag spawn = Tag.Of("spawn");
        Prefab prefab = new()
        {
            Parts =
            [
                new PrefabPart
                {
                    Components = [At(10, 0, 0)],
                    Children = [new PrefabPart { Components = [At(1, 0, 0), Tagged(spawn)] }],
                },
            ],
        };

        Transform? place = prefab.PlaceOf(spawn);

        Assert.Equal<Vector3D?>(new Vector3D(11, 0, 0), place?.Position);
    }

    [Fact]
    public void PlaceOf_takes_a_part_before_the_parts_under_it()
    {
        Tag spawn = Tag.Of("spawn");
        Prefab prefab = new()
        {
            Parts =
            [
                new PrefabPart
                {
                    Components = [At(10, 0, 0), Tagged(spawn)],
                    Children = [new PrefabPart { Components = [At(1, 0, 0), Tagged(spawn)] }],
                },
            ],
        };

        Transform? place = prefab.PlaceOf(spawn);

        Assert.Equal<Vector3D?>(new Vector3D(10, 0, 0), place?.Position);
    }

    [Fact]
    public void PlaceOf_puts_a_tagged_part_without_a_transform_where_its_parent_is()
    {
        Tag spawn = Tag.Of("spawn");
        Prefab prefab = new()
        {
            Parts =
            [
                new PrefabPart
                {
                    Components = [At(10, 0, 0)],
                    Children = [new PrefabPart { Components = [Tagged(spawn)] }],
                },
            ],
        };

        Transform? place = prefab.PlaceOf(spawn);

        Assert.Equal<Vector3D?>(new Vector3D(10, 0, 0), place?.Position);
    }

    [Fact]
    public void PlaceOf_is_null_with_no_part_tagged()
    {
        Prefab prefab = new()
        {
            Parts = [new PrefabPart { Components = [At(1, 2, 3), Tagged(Tag.Of("door"))] }],
        };

        Transform? place = prefab.PlaceOf(Tag.Of("spawn"));

        Assert.Null(place);
    }

    private static Transform At(double x, double y, double z)
    {
        return new Transform { Position = new Vector3D(x, y, z) };
    }

    private static Tags Tagged(Tag tag)
    {
        Tags tags = default;
        tags.Add(tag);
        return tags;
    }
}
