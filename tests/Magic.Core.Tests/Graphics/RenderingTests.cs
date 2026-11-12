using Magic.Graphics;
using Magic.Projects;
using Xunit;

namespace Magic.Tests.Graphics;

/// <summary>
/// The gems that render: ordered as they register from what each takes and makes, refused when two make one type or
/// they form a cycle, and pumped so each takes what the gems before it made this frame.
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

    [Fact]
    public void PumpCallsAPartThatIsNotAGem()
    {
        List<string> calls = [];
        Pump(new Renderer(calls), new World(calls, new View(1)));

        Assert.Equal(["Renderer", "World"], calls);
    }

    private static Rendering Register(params object[] gems)
    {
        Rendering rendering = new();
        foreach (object gem in gems)
        {
            rendering.Register(gem);
        }

        return rendering;
    }

    private static void Pump(params object[] gems) => Register(gems).Pump(_frame, new RenderGraph());

    private readonly record struct View(int Id);

    private readonly record struct Light(int Id);

    private readonly record struct TraceInput(View View);

    private readonly record struct ShadeInput(View View, Light? Light);

    private readonly record struct EggInput(Light Light);

    private readonly record struct HenInput(View View);

    private sealed class Plain : IGem;

    private sealed class World(List<string> calls, View? made) : IGem, IRenderInto<View>
    {
        public View? Render(in Frame frame, RenderGraph graph)
        {
            calls.Add(nameof(World));

            return made;
        }
    }

    private sealed class Trace(List<string> calls) : IGem, IRender<TraceInput, Light>
    {
        public Light? Render(in Frame frame, RenderGraph graph, in TraceInput input)
        {
            calls.Add(nameof(Trace));

            return new Light(input.View.Id + 1);
        }
    }

    private sealed class Shade(List<string> calls) : IGem, IRenderFrom<ShadeInput>
    {
        public ShadeInput? Taken { get; private set; }

        public void Render(in Frame frame, RenderGraph graph, in ShadeInput input)
        {
            calls.Add(nameof(Shade));
            Taken = input;
        }
    }

    private sealed class Lost(List<string> calls) : IGem, IRenderFrom<EggInput>
    {
        public void Render(in Frame frame, RenderGraph graph, in EggInput input) => calls.Add(nameof(Lost));
    }

    private sealed class Overlay(List<string> calls) : IGem, IRender
    {
        public void Render(in Frame frame, RenderGraph graph) => calls.Add(nameof(Overlay));
    }

    /// <summary>
    /// A gem's part, which renders but is no <see cref="IGem"/>.
    /// </summary>
    private sealed class Renderer(List<string> calls) : IRender
    {
        public void Render(in Frame frame, RenderGraph graph) => calls.Add(nameof(Renderer));
    }

    private sealed class Sun : IGem, IRenderInto<(View, Light)>
    {
        public (View, Light)? Render(in Frame frame, RenderGraph graph) => (new View(3), new Light(4));
    }

    private sealed class Egg : IGem, IRender<EggInput, View>
    {
        public View? Render(in Frame frame, RenderGraph graph, in EggInput input) => new View(input.Light.Id);
    }

    private sealed class Hen : IGem, IRender<HenInput, Light>
    {
        public Light? Render(in Frame frame, RenderGraph graph, in HenInput input) => new Light(input.View.Id);
    }
}
