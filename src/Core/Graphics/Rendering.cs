using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Magic.Debugging;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// The gems that render, each render form after every form whose output it takes: register each gem as it is made, then
/// pump them once a frame on the render thread. A gem is named in what is logged and timed by its assembly,
/// as the project lists it.
/// </summary>
internal sealed class Rendering
{
    private const string Hook = "Render";

    private readonly List<Step> _steps = [];

    /// <summary>
    /// <paramref name="gem"/>'s parts of the frame, one for each render form it implements, while it renders, until the
    /// subscription ends; nothing when it doesn't. Throws, naming the gems and the type, when it makes what another gem
    /// makes, or when it and others take what the others make round a cycle; the gems registered before go on as they
    /// were.
    /// </summary>
    public Subscription Register(IGem gem)
    {
        List<Step> steps =
        [
            .. GetForms(gem)
                .Select(form =>
                {
                    Type type = typeof(Step<,>).MakeGenericType(form.Input, form.Output);
                    return (Step)(
                        Activator.CreateInstance(type, gem) ?? throw new InvalidOperationException(type.Name)
                    );
                }),
        ];
        if (steps.Count == 0)
            return new Subscription(static () => { });

        Order([.. _steps, .. steps]);

        return new Subscription(() => Order([.. _steps.Where(other => other.Instance != gem)]));
    }

    /// <summary>
    /// Whether <paramref name="gem"/> must take what no gem registered makes, so it can never render.
    /// </summary>
    public bool HasMissingInput(IGem gem) => _steps.Exists(step => step.Instance == gem && step.Missing is not null);

    /// <summary>
    /// Every gem's parts of the frame, in order, each gem timed once for all its parts; a gem that throws is logged and renders no more, and what
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
            if (step.Missing is { } missing)
            {
                Log.Error($"Gem {step.Gem} takes {missing.Name}, which no listed gem makes, so it renders nothing.");
                _steps.RemoveAt(i--);
                continue;
            }

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

            // A gem with several steps is timed once, by its last step, for the time of all of them.
            long since = start == 0 ? 0 : start - step.Earlier;
            step.Earlier = 0;
            if (since != 0 && step.Next is { } next)
            {
                next.Earlier = Stopwatch.GetTimestamp() - since;
                continue;
            }

            Metrics.HookDurations.RecordSince(
                since,
                new KeyValuePair<string, object?>(Metrics.GemNameTag, step.Gem),
                new KeyValuePair<string, object?>(Metrics.HookTag, Hook)
            );
        }
    }

    /// <summary>
    /// What each render form <paramref name="gem"/> implements takes and makes, <see cref="None"/> for either it lacks;
    /// none when it renders nothing.
    /// </summary>
    private static IEnumerable<(Type Input, Type Output)> GetForms(IGem gem)
    {
        if (gem is IRender)
            yield return (typeof(None), typeof(None));

        foreach (Type contract in gem.GetType().GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;

            Type form = contract.GetGenericTypeDefinition();
            Type[] types = contract.GenericTypeArguments;
            if (form == typeof(IRender<,>))
                yield return (types[0], types[1]);

            if (form == typeof(IRenderFrom<>))
                yield return (types[0], typeof(None));

            if (form == typeof(IRenderInto<>))
                yield return (typeof(None), types[0]);
        }
    }

    /// <summary>
    /// <paramref name="steps"/> in place of the gems before, each after every gem that makes what it takes, in their own
    /// order otherwise, and each bound to its makers. Throws before changing anything when two make one type, or when
    /// some take what others among them make round a cycle.
    /// </summary>
    private void Order(List<Step> steps)
    {
        Dictionary<Type, Maker> makers = [];
        foreach (Step step in steps)
        {
            foreach (Maker maker in step.Outputs)
            {
                if (makers.TryGetValue(maker.Type, out Maker other))
                    throw new InvalidOperationException(
                        $"Gems {other.Step.Gem} and {step.Gem} both make {maker.Type.Name}."
                    );

                makers[maker.Type] = maker;
            }
        }

        // The first waiting gem whose makers are all placed goes next, so gems keep their order where it is free.
        List<Step> ordered = new(steps.Count);
        while (steps.Count > 0)
        {
            int next = steps.FindIndex(step =>
                step.Inputs.All(input => !makers.TryGetValue(input, out Maker maker) || ordered.Contains(maker.Step))
            );
            if (next < 0)
            {
                string gems = string.Join(", ", steps.Select(static step => step.Gem));
                throw new InvalidOperationException($"Gems {gems} each take what another of them makes.");
            }

            ordered.Add(steps[next]);
            steps.RemoveAt(next);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            Step step = ordered[i];
            step.Bind(makers);
            step.Next = ordered.Skip(i + 1).FirstOrDefault(other => other.Instance == step.Instance);
        }

        _steps.Clear();
        _steps.AddRange(ordered);
    }

    /// <summary>
    /// What a render form lacks: the input of one that takes nothing, the output of one that makes nothing.
    /// </summary>
    private readonly record struct None;

    /// <summary>
    /// A type a step makes, and where in what it made the type is: the item of a tuple, or the whole when null.
    /// </summary>
    private readonly record struct Maker(Type Type, Step Step, string? Item);

    /// <summary>
    /// A gem's render form: what it takes, what it makes, and what it made at its last run, which the gems after it
    /// take.
    /// </summary>
    private abstract class Step(IGem gem)
    {
        public IGem Instance { get; } = gem;

        public string Gem { get; } = gem.GetType().Assembly.GetName().Name ?? gem.GetType().Name;

        /// <summary>
        /// Each type it makes: none, one, or each of a tuple's.
        /// </summary>
        public abstract ImmutableArray<Maker> Outputs { get; }

        /// <summary>
        /// Each type it takes.
        /// </summary>
        public abstract ImmutableArray<Type> Inputs { get; }

        /// <summary>
        /// A type it must take that no gem makes, once bound; it can never render.
        /// </summary>
        public Type? Missing { get; protected set; }

        /// <summary>
        /// The gem's next step in the order, which is timed for both; null for its last.
        /// </summary>
        public Step? Next { get; set; }

        /// <summary>
        /// The ticks the gem's steps before it took this frame.
        /// </summary>
        public long Earlier { get; set; }

        /// <summary>
        /// Records the gem's passes, when every input it must take was made this frame, and keeps what it made.
        /// </summary>
        public abstract void Run(in Frame frame, RenderGraph graph);

        /// <summary>
        /// Builds, once, how its input is taken from <paramref name="makers"/>' outputs each frame.
        /// </summary>
        public abstract void Bind(IReadOnlyDictionary<Type, Maker> makers);
    }

    /// <summary>
    /// A step whose input is built each frame from its makers' typed outputs by a compiled constructor call, so a frame
    /// boxes nothing and reflects on nothing. Each render form is called as <see cref="IRender{TInput, TOutput}"/>,
    /// with <see cref="None"/> for what it lacks.
    /// </summary>
    private sealed class Step<TInput, TOutput>(IGem gem) : Step(gem)
        where TInput : struct
        where TOutput : struct
    {
        /// <summary>
        /// The input record's constructor with the most parameters, one for each input; none for <see cref="None"/>.
        /// </summary>
        private static readonly ConstructorInfo? _constructor = typeof(TInput)
            .GetConstructors()
            .MaxBy(static constructor => constructor.GetParameters().Length);

        private readonly Renders _render = gem switch
        {
            IRender<TInput, TOutput> both => both.Render,
            IRenderFrom<TInput> from => (in Frame frame, RenderGraph graph, in TInput input) =>
            {
                from.Render(frame, graph, input);

                return null;
            },
            IRenderInto<TOutput> into => (in Frame frame, RenderGraph graph, in TInput _) => into.Render(frame, graph),
            IRender plain => (in Frame frame, RenderGraph graph, in TInput _) =>
            {
                plain.Render(frame, graph);

                return null;
            },
            _ => throw new InvalidOperationException($"{gem.GetType().Name} has no render form."),
        };

        private Func<TInput?> _take = static () => default(TInput);

        /// <summary>
        /// What it made at its last run; null when it made nothing, wasn't run or threw.
        /// </summary>
        public TOutput? Made { get; private set; }

        public override ImmutableArray<Maker> Outputs =>
            typeof(TOutput) == typeof(None) ? []
            : typeof(ITuple).IsAssignableFrom(typeof(TOutput))
                ?
                [
                    .. typeof(TOutput).GenericTypeArguments.Select(
                        (type, item) => new Maker(type, this, $"Item{item + 1}")
                    ),
                ]
            : [new Maker(typeof(TOutput), this, null)];

        public override ImmutableArray<Type> Inputs =>
            [
                .. _constructor
                    ?.GetParameters()
                    .Select(static parameter =>
                        Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType
                    )
                    ?? [],
            ];

        public override void Run(in Frame frame, RenderGraph graph)
        {
            Made = null;
            if (_take() is { } input)
                Made = _render(frame, graph, input);
        }

        /// <summary>
        /// <c>input = madeA.HasValue &amp;&amp; ... ? new TInput(madeA.Value, madeB, ...) : null</c>, an item of a tuple
        /// taken as <c>madeA.Value.Item1</c>, and a nullable input with no maker passed as null.
        /// </summary>
        public override void Bind(IReadOnlyDictionary<Type, Maker> makers)
        {
            Missing = null;
            if (_constructor is null)
                return;

            List<Expression> arguments = [];
            List<Expression> made = [];
            foreach (ParameterInfo parameter in _constructor.GetParameters())
            {
                Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                if (!makers.TryGetValue(type, out Maker maker))
                {
                    Missing ??= parameter.ParameterType == type ? type : null;
                    arguments.Add(Expression.Default(parameter.ParameterType));
                    continue;
                }

                Expression output = Expression.Property(Expression.Constant(maker.Step), nameof(Made));
                Expression hasValue = Expression.Property(output, nameof(Nullable<>.HasValue));
                Expression value = Expression.Property(output, nameof(Nullable<>.Value));
                if (maker.Item is { } item)
                    value = Expression.Field(value, item);

                if (parameter.ParameterType == type)
                {
                    made.Add(hasValue);
                    arguments.Add(value);
                    continue;
                }

                // A nullable input is the made value, or null while its maker made nothing.
                arguments.Add(
                    Expression.Condition(
                        hasValue,
                        Expression.Convert(value, parameter.ParameterType),
                        Expression.Default(parameter.ParameterType)
                    )
                );
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

        /// <summary>
        /// The gem's render form called as <see cref="IRender{TInput, TOutput}"/>'s <c>Render</c>.
        /// </summary>
        private delegate TOutput? Renders(in Frame frame, RenderGraph graph, in TInput input);
    }
}
