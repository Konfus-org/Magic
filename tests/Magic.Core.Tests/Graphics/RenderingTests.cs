using Magic.Graphics;
using Magic.Projects;
using Xunit;

namespace Magic.Tests.Graphics;

/// <summary>
/// The gems that render: ordered from what each takes and makes, refused when an input can't be made, and pumped so
/// each takes what the gems before it made this frame.
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
    public void OrderPutsEachMakerBeforeItsTakers()
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
    public void OrderThrowsWhenNoGemMakesARequiredInput()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Order(new Trace([])));

        Assert.Contains(nameof(View), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderThrowsWhenTwoGemsMakeOneType()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Order(new World([], new View(1)), new World([], new View(2)))
        );

        Assert.Contains(nameof(View), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderThrowsWhenInputsFormACycle() =>
        Assert.Throws<InvalidOperationException>(() => Order(new Egg(), new Hen()));

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

    private static Rendering Order(params IGem[] gems)
    {
        Rendering rendering = new();
        foreach (IGem gem in gems)
        {
            rendering.Register(gem);
        }

        rendering.Order();

        return rendering;
    }

    private static void Pump(params IGem[] gems) => Order(gems).Pump(_frame, new RenderGraph());

    private readonly record struct View(int Id);

    private readonly record struct Light(int Id);

    private readonly record struct TraceInput(View View);

    private readonly record struct ShadeInput(View View, Light? Light);

    private readonly record struct EggInput(Light Light);

    private readonly record struct HenInput(View View);

    private sealed class Plain : IGem;

    private sealed class World(List<string> calls, View? made) : IGem, IRender<Nothing, View>
    {
        public View? Render(in Frame frame, RenderGraph graph, in Nothing input)
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

    private sealed class Shade(List<string> calls) : IGem, IRender<ShadeInput, Nothing>
    {
        public ShadeInput? Taken { get; private set; }

        public Nothing? Render(in Frame frame, RenderGraph graph, in ShadeInput input)
        {
            calls.Add(nameof(Shade));
            Taken = input;

            return default(Nothing);
        }
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
