using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;

namespace Magic.Tests.Contexts.Assets;

public sealed class PrefabTests
{
    [Fact]
    public void FindWith_finds_a_tagged_top_part()
    {
        Tag spawn = Tag.Of("spawn");
        Prefab prefab = new()
        {
            Parts = [new PrefabPart { Components = [At(1, 2, 3), Tagged(spawn)] }],
        };

        Transform? place = prefab.FindWith(spawn);

        Assert.Equal(new Vector3(1, 2, 3), place?.Position);
    }

    [Fact]
    public void FindWith_places_a_tagged_child_under_its_parent()
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

        Transform? place = prefab.FindWith(spawn);

        Assert.Equal(new Vector3(11, 0, 0), place?.Position);
    }

    [Fact]
    public void FindWith_takes_a_part_before_the_parts_under_it()
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

        Transform? place = prefab.FindWith(spawn);

        Assert.Equal(new Vector3(10, 0, 0), place?.Position);
    }

    [Fact]
    public void FindWith_puts_a_tagged_part_without_a_transform_where_its_parent_is()
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

        Transform? place = prefab.FindWith(spawn);

        Assert.Equal(new Vector3(10, 0, 0), place?.Position);
    }

    [Fact]
    public void FindWith_is_null_with_no_part_tagged()
    {
        Prefab prefab = new()
        {
            Parts = [new PrefabPart { Components = [At(1, 2, 3), Tagged(Tag.Of("door"))] }],
        };

        Transform? place = prefab.FindWith(Tag.Of("spawn"));

        Assert.Null(place);
    }

    private static Transform At(float x, float y, float z) => new() { Position = new Vector3(x, y, z) };

    private static Tags Tagged(Tag tag)
    {
        Tags tags = default;
        tags.Add(tag);

        return tags;
    }
}
