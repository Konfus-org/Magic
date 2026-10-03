using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Systems;
using Magic.Systems.Streaming;
using System.Numerics;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The transform system on the real Flecs gem.
/// </summary>
public sealed class TransformSystemTests
{
    [Fact]
    public void A_world_transform_composes_through_every_parent()
    {
        using FlecsEcs ecs = new();
        using TagSystem tags = new(ecs);
        using TransformSystem transforms = new(ecs);
        Handle root = Spawn(ecs, new Vector3(1, 0, 0));
        Handle child = Spawn(ecs, new Vector3(0, 2, 0), root);
        Handle grandchild = Spawn(ecs, new Vector3(0, 0, 3), child);

        tags.Run(default);
        transforms.Run(default);

        Assert.Equal(new Vector3(1, 2, 3), ecs.Get<WorldTransform>(grandchild).Value.Translation);
    }

    [Fact]
    public void A_moved_parent_carries_its_descendants()
    {
        using FlecsEcs ecs = new();
        using TagSystem tags = new(ecs);
        using TransformSystem transforms = new(ecs);
        Handle root = Spawn(ecs, new Vector3(1, 0, 0));
        Handle child = Spawn(ecs, new Vector3(0, 2, 0), root);
        tags.Run(default);
        transforms.Run(default);
        ecs.Get<Transform>(root).Position = new Vector3(10, 0, 0);

        tags.Run(default);
        transforms.Run(default);

        Assert.Equal(new Vector3(10, 2, 0), ecs.Get<WorldTransform>(child).Value.Translation);
    }

    [Fact]
    public void A_static_entity_keeps_its_first_world_transform()
    {
        using FlecsEcs ecs = new();
        using TagSystem tags = new(ecs);
        using TransformSystem transforms = new(ecs);
        Handle rock = Spawn(ecs, new Vector3(1, 1, 1), isStatic: true);
        tags.Run(default);
        transforms.Run(default);
        ecs.Get<Transform>(rock).Position = new Vector3(9, 9, 9);

        tags.Run(default);
        transforms.Run(default);

        Assert.Equal(new Vector3(1, 1, 1), ecs.Get<WorldTransform>(rock).Value.Translation);
    }

    [Fact]
    public void Setting_the_static_tag_again_recomputes_the_world_transform()
    {
        using FlecsEcs ecs = new();
        using TagSystem tags = new(ecs);
        using TransformSystem transforms = new(ecs);
        Handle rock = Spawn(ecs, new Vector3(1, 1, 1), isStatic: true);
        tags.Run(default);
        transforms.Run(default);
        ecs.Get<Transform>(rock).Position = new Vector3(9, 9, 9);
        ecs.Set(rock, Tags.Of(Tag.Static));

        tags.Run(default);
        transforms.Run(default);

        Assert.Equal(new Vector3(9, 9, 9), ecs.Get<WorldTransform>(rock).Value.Translation);
    }

    [Fact]
    public void A_static_entity_recomputed_once_settles_again()
    {
        using FlecsEcs ecs = new();
        using TagSystem tags = new(ecs);
        using TransformSystem transforms = new(ecs);
        Handle rock = Spawn(ecs, new Vector3(1, 1, 1), isStatic: true);
        tags.Run(default);
        transforms.Run(default);
        ecs.Set(rock, Tags.Of(Tag.Static));
        tags.Run(default);
        transforms.Run(default);
        ecs.Get<Transform>(rock).Position = new Vector3(5, 5, 5);

        tags.Run(default);
        transforms.Run(default);

        Assert.Equal(new Vector3(1, 1, 1), ecs.Get<WorldTransform>(rock).Value.Translation);
    }

    private static Handle Spawn(FlecsEcs ecs, Vector3 position, Handle parent = default, bool isStatic = false)
    {
        Handle entity = ecs.Create(null, parent);
        ecs.Set(entity, new Transform { Position = position });
        if (isStatic)
            ecs.Set(entity, Tags.Of(Tag.Static));

        return entity;
    }
}
