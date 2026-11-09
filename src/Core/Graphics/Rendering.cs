using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Magic.Debugging;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// The gems that render, each after every gem whose output it takes: register each as it is made, order them once all
/// are, then pump them once a frame on the render thread. A gem is named in what is logged and timed by its assembly,
/// as the project lists it.
/// </summary>
internal sealed class Rendering
{
    private const string Hook = "Render";

    private readonly List<Step> _steps = [];

    /// <summary>
    /// <paramref name="gem"/>'s part of the frame while it renders, until the subscription ends; nothing when it doesn't.
    /// </summary>
    public Subscription Register(IGem gem)
    {
        Type? contract = Array.Find(
            gem.GetType().GetInterfaces(),
            static contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRender<,>)
        );
        if (contract is null)
            return new Subscription(static () => { });

        Type type = typeof(Step<,>).MakeGenericType(contract.GenericTypeArguments);
        Step step = (Step)(Activator.CreateInstance(type, gem) ?? throw new InvalidOperationException(type.Name));
        _steps.Add(step);

        return new Subscription(() => _steps.Remove(step));
    }

    /// <summary>
    /// Puts each gem after every gem that makes what it takes, in the order they were registered otherwise, and binds
    /// each to its makers. Throws, naming the gems and the type, when a gem takes what no gem makes and must, when two
    /// make one type, or when some take what others among them make round a cycle.
    /// </summary>
    public void Order()
    {
        // Nothing renders until every check passes.
        List<Step> waiting = [.. _steps];
        _steps.Clear();
        Dictionary<Type, Step> makers = [];
        foreach (Step step in waiting.Where(static step => step.Output != typeof(Nothing)))
        {
            if (makers.TryGetValue(step.Output, out Step? other))
                throw new InvalidOperationException($"Gems {other.Gem} and {step.Gem} both make {step.Output.Name}.");

            makers[step.Output] = step;
        }

        foreach (Step step in waiting)
        {
            foreach ((Type type, bool isRequired) in step.Inputs)
            {
                if (isRequired && !makers.ContainsKey(type))
                    throw new InvalidOperationException(
                        $"Gem {step.Gem} takes {type.Name}, which no listed gem makes."
                    );
            }
        }

        // The first waiting gem whose makers are all placed goes next, so gems keep their order where it is free.
        List<Step> ordered = new(waiting.Count);
        while (waiting.Count > 0)
        {
            int next = waiting.FindIndex(step =>
                step.Inputs.All(input => !makers.TryGetValue(input.Type, out Step? maker) || ordered.Contains(maker))
            );
            if (next < 0)
            {
                string gems = string.Join(", ", waiting.Select(static step => step.Gem));
                throw new InvalidOperationException($"Gems {gems} each take what another of them makes.");
            }

            ordered.Add(waiting[next]);
            waiting.RemoveAt(next);
        }

        foreach (Step step in ordered)
        {
            step.Bind(makers);
        }

        _steps.AddRange(ordered);
    }

    /// <summary>
    /// Every gem's part of the frame, in order, each timed; a gem that throws is logged and renders no more, and what
    /// it would have made stays unmade, so every gem that must take it renders no more either.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the frame; the others go on."
    )]
    public void Pump(in Frame frame, RenderGraph graph)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            Step step = _steps[i];
            long start = Metrics.HookDurations.Start();
            try
            {
                step.Run(frame, graph);
            }
            catch (Exception ex)
            {
                Log.Error($"Gem {step.Gem} failed in Render and stops rendering: {ex}");
                _steps.RemoveAt(i--);
            }

            Metrics.HookDurations.RecordSince(
                start,
                new KeyValuePair<string, object?>(Metrics.GemNameTag, step.Gem),
                new KeyValuePair<string, object?>(Metrics.HookTag, Hook)
            );
        }
    }

    /// <summary>
    /// A gem's <see cref="IRender{TInput, TOutput}"/>: what it takes, what it makes, and what it made at its last run,
    /// which the gems after it take.
    /// </summary>
    private abstract class Step(IGem gem)
    {
        public string Gem { get; } = gem.GetType().Assembly.GetName().Name ?? gem.GetType().Name;

        /// <summary>
        /// The type it makes; <see cref="Nothing"/> is none another gem can take.
        /// </summary>
        public abstract Type Output { get; }

        /// <summary>
        /// Each type it takes, and whether a gem must make it.
        /// </summary>
        public abstract ImmutableArray<(Type Type, bool IsRequired)> Inputs { get; }

        /// <summary>
        /// Records the gem's passes, when every input it must take was made this frame, and keeps what it made.
        /// </summary>
        public abstract void Run(in Frame frame, RenderGraph graph);

        /// <summary>
        /// Builds, once, how its input is taken from <paramref name="makers"/>' outputs each frame.
        /// </summary>
        public abstract void Bind(IReadOnlyDictionary<Type, Step> makers);
    }

    /// <summary>
    /// A step whose input is built each frame from its makers' typed outputs by a compiled constructor call, so a frame
    /// boxes nothing and reflects on nothing.
    /// </summary>
    private sealed class Step<TInput, TOutput>(IGem gem) : Step(gem)
        where TInput : struct
        where TOutput : struct
    {
        /// <summary>
        /// The input record's constructor with the most parameters, one for each input; none for <see cref="Nothing"/>.
        /// </summary>
        private static readonly ConstructorInfo? _constructor = typeof(TInput)
            .GetConstructors()
            .MaxBy(static constructor => constructor.GetParameters().Length);

        private readonly IRender<TInput, TOutput> _render = (IRender<TInput, TOutput>)gem;

        private Func<TInput?> _take = static () => default(TInput);

        /// <summary>
        /// What it made at its last run; null when it made nothing, wasn't run or threw.
        /// </summary>
        public TOutput? Made { get; private set; }

        public override Type Output => typeof(TOutput);

        public override ImmutableArray<(Type Type, bool IsRequired)> Inputs =>
            [
                .. _constructor
                    ?.GetParameters()
                    .Select(static parameter =>
                        Nullable.GetUnderlyingType(parameter.ParameterType) is { } type
                            ? (type, false)
                            : (parameter.ParameterType, true)
                    )
                    ?? [],
            ];

        public override void Run(in Frame frame, RenderGraph graph)
        {
            Made = null;
            if (_take() is { } input)
                Made = _render.Render(frame, graph, input);
        }

        /// <summary>
        /// <c>input = madeA.HasValue &amp;&amp; ... ? new TInput(madeA.Value, madeB, ...) : null</c>, a nullable input with no
        /// maker passed as null.
        /// </summary>
        public override void Bind(IReadOnlyDictionary<Type, Step> makers)
        {
            if (_constructor is null)
                return;

            List<Expression> arguments = [];
            List<Expression> made = [];
            foreach (ParameterInfo parameter in _constructor.GetParameters())
            {
                Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                if (!makers.TryGetValue(type, out Step? maker))
                {
                    arguments.Add(Expression.Default(parameter.ParameterType));
                    continue;
                }

                Expression output = Expression.Property(Expression.Constant(maker), nameof(Made));
                if (parameter.ParameterType != type)
                {
                    arguments.Add(output);
                    continue;
                }

                made.Add(Expression.Property(output, nameof(Nullable<>.HasValue)));
                arguments.Add(Expression.Property(output, nameof(Nullable<>.Value)));
            }

            Expression input = Expression.Convert(Expression.New(_constructor, arguments), typeof(TInput?));
            Expression body =
                made.Count == 0
                    ? input
                    : Expression.Condition(
                        made.Aggregate(Expression.AndAlso),
                        input,
                        Expression.Default(typeof(TInput?))
                    );
            _take = Expression.Lambda<Func<TInput?>>(body).Compile();
        }
    }
}
