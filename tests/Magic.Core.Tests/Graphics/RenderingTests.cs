using Magic.Graphics;
using Magic.Projects;
using Xunit;

namespace Magic.Tests.Graphics;

/// <summary>
/// The gems' parts that render: ordered as they register from what each takes and makes, refused when two make one
/// type or they form a cycle, and pumped so each takes what the parts before it made this frame.
/// </summary>
public sealed class RenderingTests
{
    private static readonly Frame _frame = new()
    {
        Index = 0,
        AppSec = 0,
        AppDeltaSec = 0,
        SimSec = 0,
        SimDeltaSec = 0,
    };

    [Fact]
    public void RegisterPutsEachMakerBeforeItsTakers()
    {
        List<string> calls = [];
        Pump(new Shade(calls), new Trace(calls), new World(calls, new View(1)));

        Assert.Equal(["World", "Trace", "Shade"], calls);
    }

    [Fact]
    public void RegisterLeavesAGemThatDoesNotRenderOut()
    {
        List<string> calls = [];
        Pump(new Plain(), new World(calls, new View(1)));

        Assert.Equal(["World"], calls);
    }

    [Fact]
    public void PumpDropsAGemWhoseRequiredInputNoGemMakes()
    {
        List<string> calls = [];
        Pump(new Lost(calls), new World(calls, new View(1)));

        Assert.Equal(["World"], calls);
    }

    [Fact]
    public void RegisterThrowsWhenTwoGemsMakeOneType()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Register(new World([], new View(1)), new World([], new View(2)))
        );

        Assert.Contains(nameof(View), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterThrowsWhenInputsFormACycle() =>
        Assert.Throws<InvalidOperationException>(() => Register(new Egg(), new Hen()));

    [Fact]
    public void PumpHandsEachOutputToItsTakers()
    {
        Shade shade = new([]);
        Pump(new World([], new View(7)), new Trace([]), shade);

        Assert.Equal(new ShadeInput(new View(7), new Light(8)), shade.Taken);
    }

    [Fact]
    public void PumpGivesNullForANullableInputNoListedGemMakes()
    {
        Shade shade = new([]);
        Pump(new World([], new View(7)), shade);

        Assert.Equal(new ShadeInput(new View(7), null), shade.Taken);
    }

    [Fact]
    public void PumpSkipsAGemWhoseRequiredInputWasNotMadeThisFrame()
    {
        Shade shade = new([]);
        Pump(new World([], null), new Trace([]), shade);

        Assert.Null(shade.Taken);
    }

    [Fact]
    public void PumpCallsAGemThatTakesAndMakesNothingInItsOrder()
    {
        List<string> calls = [];
        Pump(new Overlay(calls), new World(calls, new View(1)));

        Assert.Equal(["Overlay", "World"], calls);
    }

    [Fact]
    public void PumpHandsEachPartOfATupleOutputToItsOwnTakers()
    {
        Shade shade = new([]);
        Pump(new Sun(), shade);

        Assert.Equal(new ShadeInput(new View(3), new Light(4)), shade.Taken);
    }

    private static Rendering Register(params object[] parts)
    {
        Rendering rendering = new();
        foreach (object part in parts)
        {
            rendering.Register(part);
        }

        return rendering;
    }

    private static void Pump(params object[] parts) => Register(parts).Pump(_frame, new RenderGraph());

    private readonly record struct View(int Id);

    private readonly record struct Light(int Id);

    private readonly record struct TraceInput(View View);

    private readonly record struct ShadeInput(View View, Light? Light);

    private readonly record struct EggInput(Light Light);

    private readonly record struct HenInput(View View);

    private sealed class Plain;

    private sealed class World(List<string> calls, View? made) : IRenderInto<View>
    {
        public View? Render(in Frame frame, RenderGraph graph)
        {
            calls.Add(nameof(World));

            return made;
        }
    }

    private sealed class Trace(List<string> calls) : IRender<TraceInput, Light>
    {
        public Light? Render(in Frame frame, RenderGraph graph, in TraceInput input)
        {
            calls.Add(nameof(Trace));

            return new Light(input.View.Id + 1);
        }
    }

    private sealed class Shade(List<string> calls) : IRenderFrom<ShadeInput>
    {
        public ShadeInput? Taken { get; private set; }

        public void Render(in Frame frame, RenderGraph graph, in ShadeInput input)
        {
            calls.Add(nameof(Shade));
            Taken = input;
        }
    }

    private sealed class Lost(List<string> calls) : IRenderFrom<EggInput>
    {
        public void Render(in Frame frame, RenderGraph graph, in EggInput input) => calls.Add(nameof(Lost));
    }

    private sealed class Overlay(List<string> calls) : IRender
    {
        public void Render(in Frame frame, RenderGraph graph) => calls.Add(nameof(Overlay));
    }

    private sealed class Sun : IRenderInto<(View, Light)>
    {
        public (View, Light)? Render(in Frame frame, RenderGraph graph) => (new View(3), new Light(4));
    }

    private sealed class Egg : IRender<EggInput, View>
    {
        public View? Render(in Frame frame, RenderGraph graph, in EggInput input) => new View(input.Light.Id);
    }

    private sealed class Hen : IRender<HenInput, Light>
    {
        public Light? Render(in Frame frame, RenderGraph graph, in HenInput input) => new Light(input.View.Id);
    }
}
