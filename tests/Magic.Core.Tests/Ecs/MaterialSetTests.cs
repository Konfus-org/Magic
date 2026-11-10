using Magic.Assets;
using Magic.Ecs.Components;
using Xunit;

namespace Magic.Tests.Ecs;

/// <summary>
/// A renderer's material set: equal slots are one set, and the sets made in code that no renderer holds any more are
/// freed.
/// </summary>
public sealed class MaterialSetTests
{
    [Fact]
    public void AnOverrideChangedEveryFrameKeepsTheStoreFlat()
    {
        MaterialSet held = MaterialSet.Of(new MaterialInstance { Handle = new Handle<Material>(7) });
        HashSet<int> renderers = [];
        int settled = 0;
        for (int frame = 0; frame < 100; frame++)
        {
            MaterialInstance slot = held[0];
            slot.Overrides["tint"] = new MaterialParam { X = frame };
            held = held.With(0, slot);
            renderers.Clear();
            renderers.Add(held.Id);
            MaterialSet.Sweep(renderers);
            if (frame == 10)
                settled = MaterialSet.StoredCount;
        }

        Assert.Equal(settled, MaterialSet.StoredCount);
        Assert.Equal(99, held[0].Overrides["tint"].X);
    }

    [Fact]
    public void EqualSlotsAreOneSet()
    {
        MaterialInstance stone = new() { Handle = new Handle<Material>(8) };

        Assert.Equal(MaterialSet.Of(stone).Id, MaterialSet.Empty.With(0, stone).Id);
    }
}
