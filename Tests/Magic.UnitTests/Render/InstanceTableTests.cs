using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Mathematics;
using Magic.UnitTests.Fakes;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class InstanceTableTests
{
    [Fact]
    public void An_added_instance_is_alive()
    {
        InstanceTable table = Table();

        uint slot = Add(table, Vector3.Zero);

        Assert.True(table.IsAlive(slot));
    }

    [Fact]
    public void A_removed_instance_is_not_alive()
    {
        InstanceTable table = Table();
        uint slot = Add(table, Vector3.Zero);

        table.Remove(slot);

        Assert.False(table.IsAlive(slot));
    }

    [Fact]
    public void An_instance_remembers_its_model()
    {
        InstanceTable table = Table();

        uint slot = table.Add(514, 3, UnitSphere(), default, default, InstanceFlags.None, Matrix4x4.Identity, 0, isStatic: false);

        Assert.Equal(514ul, table.ModelOf(slot));
    }

    [Fact]
    public void An_instance_remembers_its_material()
    {
        InstanceTable table = Table();

        uint slot = table.Add(514, 3, UnitSphere(), new Handle<Material>(77), default, InstanceFlags.None, Matrix4x4.Identity, 0, isStatic: false);

        Assert.Equal(new Handle<Material>(77), table.MaterialOf(slot));
    }

    [Fact]
    public void A_linked_part_follows_the_one_it_was_linked_to()
    {
        InstanceTable table = Table();
        uint first = Add(table, Vector3.Zero);
        uint second = Add(table, Vector3.Zero);

        table.Link(first, second);

        Assert.Equal(second, table.NextPart(first));
    }

    [Fact]
    public void An_unlinked_part_ends_its_chain()
    {
        InstanceTable table = Table();

        uint slot = Add(table, Vector3.Zero);

        Assert.Equal(InstanceTable.End, table.NextPart(slot));
    }

    [Fact]
    public void A_freed_slot_is_handed_out_again()
    {
        InstanceTable table = Table();
        uint first = Add(table, Vector3.Zero);
        table.Remove(first);

        uint again = Add(table, Vector3.Zero);

        Assert.Equal(first, again);
    }

    [Fact]
    public void A_slot_taken_again_starts_a_chain_of_its_own()
    {
        InstanceTable table = Table();
        uint first = Add(table, Vector3.Zero);
        table.Link(first, Add(table, Vector3.Zero));
        table.Remove(first);

        uint again = Add(table, Vector3.Zero);

        Assert.Equal(InstanceTable.End, table.NextPart(again));
    }

    [Fact]
    public void A_full_page_overflows_into_a_new_page()
    {
        InstanceTable table = Table();
        for (int i = 0; i < InstanceTable.PageSize; i++)
            Add(table, Vector3.Zero);

        uint overflow = Add(table, Vector3.Zero);

        Assert.Equal((uint)InstanceTable.PageSize, overflow);
    }

    [Fact]
    public void A_static_instance_grows_the_bounds_of_the_cell_under_it()
    {
        InstanceTable table = Table();
        Add(table, new Vector3(10, 0, 10), isStatic: true);

        Add(table, new Vector3(20, 5, 10), isStatic: true);

        Assert.Equal(new Vector4(21, 6, 11, 0), table.Cells[1].AabbMax);
    }

    [Fact]
    public void A_static_instance_puts_its_page_in_the_cell_under_it()
    {
        InstanceTable table = Table();

        uint slot = Add(table, new Vector3(10, 0, 10), isStatic: true);

        Assert.Equal(1u, table.Pages[(int)(slot / InstanceTable.PageSize)].Cell);
    }

    [Fact]
    public void A_moving_instance_lives_in_the_always_visible_cell()
    {
        InstanceTable table = Table();

        uint slot = Add(table, new Vector3(10, 0, 10));

        Assert.Equal(0u, table.Pages[(int)(slot / InstanceTable.PageSize)].Cell);
    }

    private static InstanceTable Table()
    {
        return new InstanceTable(new FakeRendering(), 16);
    }

    private static BoundingSphere UnitSphere()
    {
        return new BoundingSphere(Vector3.Zero, 1f);
    }

    private static uint Add(InstanceTable table, Vector3 position, bool isStatic = false)
    {
        return table.Add(1, 1, UnitSphere(), default, default, InstanceFlags.None, Matrix4x4.CreateTranslation(position), 0, isStatic);
    }
}
