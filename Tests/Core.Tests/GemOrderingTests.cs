using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using TestGems.Contracts;

namespace Core.Tests;

public sealed class GemOrderingTests
{
    private sealed record Node(string Name, Type[] Provides, Type[] Requires);

    private static (List<Node> Order, List<(string Name, string Why)> Skipped) Sort(params Node[] nodes)
        => Sort(_ => false, nodes);

    private static (List<Node> Order, List<(string Name, string Why)> Skipped) Sort(Func<Type, bool> hostProvides, params Node[] nodes)
    {
        List<(string, string)> skipped = [];
        List<Node> order = GemOrdering.TopoSort(
            nodes,
            n => n.Provides,
            n => n.Requires,
            hostProvides,
            n => (n.Provides.Contains(typeof(ILogger)) ? 0 : 1, n.Name),
            (n, why) => skipped.Add((n.Name, why)));
        return (order, skipped);
    }

    [Fact]
    public void Providers_come_before_consumers_whatever_the_input_order()
    {
        (List<Node> order, _) = Sort(
            new Node("Consumer", [], [typeof(ITestService)]),
            new Node("Provider", [typeof(ITestService)], []));

        Assert.Equal(["Provider", "Consumer"], order.Select(n => n.Name));
    }

    [Fact]
    public void Logger_exporters_go_first_among_ready_nodes()
    {
        (List<Node> order, _) = Sort(
            new Node("A", [], []),
            new Node("Logger", [typeof(ILogger)], []),
            new Node("B", [], []));

        Assert.Equal("Logger", order[0].Name);
    }

    [Fact]
    public void Host_services_create_no_edges_and_no_skips()
    {
        (List<Node> order, List<(string, string)> skipped) = Sort(
            t => t == typeof(SystemRegistry),
            new Node("A", [], [typeof(SystemRegistry)]));

        Assert.Single(order);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Missing_dependency_skips_the_node_and_its_dependents()
    {
        (List<Node> order, List<(string Name, string Why)> skipped) = Sort(
            new Node("Consumer", [typeof(ITestConsumer)], [typeof(IWindow)]),
            new Node("Downstream", [], [typeof(ITestConsumer)]),
            new Node("Fine", [], []));

        Assert.Equal(["Fine"], order.Select(n => n.Name));
        Assert.Contains(skipped, s => s.Name == "Consumer" && s.Why.Contains(nameof(IWindow)));
        Assert.Contains(skipped, s => s.Name == "Downstream" && s.Why.Contains("Consumer"));
    }

    [Fact]
    public void Cycles_are_skipped()
    {
        (List<Node> order, List<(string Name, string Why)> skipped) = Sort(
            new Node("A", [typeof(IWindow)], [typeof(IWindowFactory)]),
            new Node("B", [typeof(IWindowFactory)], [typeof(IWindow)]));

        Assert.Empty(order);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Contains("cycle", s.Why));
    }

    [Fact]
    public void Duplicate_providers_keep_the_first_one()
    {
        (List<Node> order, List<(string, string)> skipped) = Sort(
            new Node("First", [typeof(ITestService)], []),
            new Node("Second", [typeof(ITestService)], []),
            new Node("Consumer", [], [typeof(ITestService)]));

        Assert.Empty(skipped);
        Assert.True(order.FindIndex(n => n.Name == "First") < order.FindIndex(n => n.Name == "Consumer"));
    }
}
