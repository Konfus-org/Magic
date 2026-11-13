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
/// The gems that render, each render form after each form whose output it takes.
/// </summary>
/// <remarks>
/// Register each gem's parts as the gem is made, then pump them once a frame on the render thread. Logs and timings
/// name a gem by its assembly, as the project lists it.
/// </remarks>
internal sealed class Rendering
{
    private const string Hook = "Render";

    /// <summary>
    /// The seconds a step that threw waits before it is tried again.
    /// </summary>
    private const double RetryPauseSec = 3;

    private readonly List<Step> _steps = [];

    /// <summary>
    /// The step that makes each type, as the steps were last ordered.
    /// </summary>
    private Dictionary<Type, Maker> _makers = [];

    /// <summary>
    /// Where in <see cref="_steps"/> the steps that run once for each view are: after every step that runs once
    /// before the views, and before every step that takes and makes nothing.
    /// </summary>
    private Range _viewSteps;

    /// <summary>
    /// Reads the views that the frame draws from the last run of the step that makes <see cref="RenderViews"/>; null
    /// when no step makes them or it made nothing.
    /// </summary>
    private Func<RenderViews?> _getViews = static () => null;

    /// <summary>
    /// Adds a step of the frame for each render form that <paramref name="part"/> implements, until the subscription
    /// ends.
    /// </summary>
    /// <remarks>
    /// A part with no render form adds nothing. It throws, and names the gems and the type, when the part makes what
    /// another gem makes, or when the inputs make a cycle. Then the gems registered before stay as they were.
    /// </remarks>
    public Subscription Register(object part)
    {
        List<Step> steps =
        [
            .. GetForms(part)
                .Select(form =>
                {
                    Type type = typeof(Step<,>).MakeGenericType(form.Input, form.Output);
                    return (Step)(
                        Activator.CreateInstance(type, part) ?? throw new InvalidOperationException(type.Name)
                    );
                }),
        ];
        if (steps.Count == 0)
            return new Subscription(static () => { });

        Order([.. _steps, .. steps]);

        return new Subscription(() => Order([.. _steps.Where(other => other.Part != part)]));
    }

    /// <summary>
    /// Whether <paramref name="part"/> must take what no registered gem makes. It does not render until such a gem
    /// registers.
    /// </summary>
    public bool HasMissingInput(object part) => _steps.Exists(step => step.Part == part && step.Missing is not null);

    /// <summary>
    /// Whether no gem makes <paramref name="type"/>, or its step is out this frame. See
    /// <see cref="RenderGraph.IsMakerOut{T}"/>.
    /// </summary>
    public bool IsMakerOut(Type type) => !_makers.TryGetValue(type, out Maker maker) || maker.Step.IsOut;

    /// <summary>
    /// Records each gem's steps of the frame in order into <paramref name="graph"/>, and times each gem one time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The steps before the views run once. Then the steps of a view run once for each view that the frame draws, and
    /// a view's passes come after every pass of the view before. The steps that take and make nothing run last.
    /// </para>
    /// <para>
    /// A step that takes what no gem makes, or that threw less than <see cref="RetryPauseSec"/> ago, is skipped as a
    /// <see cref="RenderFailure.Pass"/> failure. What it makes stays unmade: a step that must take it is skipped too,
    /// and a step that can take it runs without it.
    /// </para>
    /// </remarks>
    public void Pump(in Frame frame, RenderGraph graph)
    {
        graph.StartFailures();
        (int first, int count) = _viewSteps.GetOffsetAndLength(_steps.Count);
        for (int i = 0; i < first; i++)
        {
            Run(_steps[i], _steps[i].Next, frame, graph);
        }

        // One view unless the step that makes the views, which ran before them, tells of more.
        int views = _getViews()?.Count ?? 1;
        for (int view = 0; view < views; view++)
        {
            if (view > 0)
                graph.OrderPasses();

            graph.View = view;
            // A gem whose last step is one of a view is timed after the last view, for all of them.
            for (int i = first; i < first + count; i++)
            {
                Step step = _steps[i];
                Run(step, step.Next ?? (view < views - 1 ? step : null), frame, graph);
            }
        }

        graph.View = 0;
        for (int i = first + count; i < _steps.Count; i++)
        {
            Run(_steps[i], _steps[i].Next, frame, graph);
        }
    }

    /// <summary>
    /// Gets what each render form of <paramref name="part"/> takes and makes, with <see cref="None"/> for a side it
    /// lacks.
    /// </summary>
    private static IEnumerable<(Type Input, Type Output)> GetForms(object part)
    {
        if (part is IRender)
            yield return (typeof(None), typeof(None));

        foreach (Type contract in part.GetType().GetInterfaces())
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
    /// Runs <paramref name="step"/>, unless it lacks an input or waits for its retry, and tells the frame when it is
    /// out.
    /// </summary>
    /// <remarks>
    /// Its time goes on to <paramref name="timedBy"/>, the step of its gem that runs after it. With none, the time is
    /// recorded for the gem.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is a failure of the frame and the gem leaves it; the others go on."
    )]
    private static void Run(Step step, Step? timedBy, in Frame frame, RenderGraph graph)
    {
        long start = Metrics.HookDurations.Start();
        int told = graph.ToldCount;
        if (step.Missing is null && (step.RetrySec is not { } retry || frame.AppSec >= retry))
        {
            try
            {
                step.Run(frame, graph);
                step.RetrySec = null;
            }
            catch (Exception ex)
            {
                // A step that throws again at a retry keeps the first throw's texts: only a new failure is logged.
                if (step.RetrySec is null)
                    step.Failure = ($"{step.Gem} stopped rendering", $"It threw in Render: {ex}");

                step.RetrySec = frame.AppSec + RetryPauseSec;
                graph.LeaveOutOpenPass();
            }
        }

        // Tell the failure in each frame that the step is out. Fail logs it only when it is new.
        bool isSkipped = step.Missing is not null || step.RetrySec is not null;
        if (isSkipped)
            graph.Fail(RenderFailure.Pass, step.Failure.What, step.Failure.Reason);

        step.IsOut = isSkipped || (!step.HasMade && graph.ToldCount > told);

        // A gem with several steps is timed once a frame, by the last run of its last step, for the time of all of
        // them in every view.
        long since = start == 0 ? 0 : start - step.Earlier;
        step.Earlier = 0;
        if (since != 0 && timedBy is { } next)
        {
            next.Earlier += Stopwatch.GetTimestamp() - since;
            return;
        }

        Metrics.HookDurations.RecordSince(
            since,
            new KeyValuePair<string, object?>(Metrics.GemNameTag, step.Gem),
            new KeyValuePair<string, object?>(Metrics.HookTag, Hook)
        );
    }

    /// <summary>
    /// Replaces the steps with <paramref name="steps"/>, each after the steps that make what it takes, and binds each
    /// to its makers.
    /// </summary>
    /// <remarks>
    /// A step that takes and makes nothing goes last, so it sees all of the frame's failures. Other steps keep their
    /// order where they can. It throws before it changes anything when two steps make one type, or the inputs make a
    /// cycle.
    /// </remarks>
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

        // Each step that takes the views, and each that takes what such a step makes, runs once for each view.
        HashSet<Step> viewSteps = [.. steps.Where(static step => step.Inputs.Contains(typeof(RenderViews)))];
        Func<RenderViews?> getViews = makers.TryGetValue(typeof(RenderViews), out Maker views)
            ? views.Step.CreateViewsReader(views.Item)
            : static () => null;
        while (
            steps.Find(step =>
                !viewSteps.Contains(step)
                && step.Inputs.Any(input =>
                    makers.TryGetValue(input, out Maker maker) && viewSteps.Contains(maker.Step)
                )
            )
                is { } taker
        )
        {
            viewSteps.Add(taker);
        }

        // The first waiting gem whose makers are all placed goes next, so gems keep their order where it is free.
        // The steps of a view wait for those before the views, and a gem that takes and makes nothing waits at the
        // end; the sort keeps the order of equals.
        steps =
        [
            .. steps.OrderBy(step =>
                step.Inputs.IsEmpty && step.Outputs.IsEmpty ? 2
                : viewSteps.Contains(step) ? 1
                : 0
            ),
        ];
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
            step.Next = ordered.Skip(i + 1).FirstOrDefault(other => other.Part == step.Part);
        }

        int firstViewStep = ordered.FindIndex(viewSteps.Contains);
        _viewSteps = firstViewStep < 0 ? ^0.. : firstViewStep..(firstViewStep + viewSteps.Count);
        _getViews = getViews;
        _steps.Clear();
        _steps.AddRange(ordered);
        _makers = makers;
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
    /// A gem's render form: what it takes, what it makes, and what it made at its last run for the gems after it.
    /// </summary>
    private abstract class Step(object part)
    {
        public object Part { get; } = part;

        public string Gem { get; } = GemRegistry.GetGemName(part.GetType());

        /// <summary>
        /// Each type it makes: none, one, or each of a tuple's.
        /// </summary>
        public abstract ImmutableArray<Maker> Outputs { get; }

        /// <summary>
        /// Each type it takes.
        /// </summary>
        public abstract ImmutableArray<Type> Inputs { get; }

        /// <summary>
        /// A type it must take that no gem makes, after it is bound. It does not render until such a gem registers.
        /// </summary>
        public Type? Missing { get; protected set; }

        /// <summary>
        /// The <see cref="Frame.AppSec"/> from which a step that threw is tried again; null while it renders.
        /// </summary>
        public double? RetrySec { get; set; }

        /// <summary>
        /// Why it does not render, made one time when its input is missing or it throws, so a frame allocates no text.
        /// </summary>
        public (string What, string Reason) Failure { get; set; }

        /// <summary>
        /// Whether it made nothing this frame and told why: it was skipped, or it told a failure itself.
        /// </summary>
        public bool IsOut { get; set; }

        /// <summary>
        /// Whether its last run made what it makes.
        /// </summary>
        public abstract bool HasMade { get; }

        /// <summary>
        /// The gem's next step in the order, which is timed for both; null for the gem's last step.
        /// </summary>
        public Step? Next { get; set; }

        /// <summary>
        /// The ticks the gem's steps before it took this frame.
        /// </summary>
        public long Earlier { get; set; }

        /// <summary>
        /// Records the gem's passes when each input it must take was made this frame, and keeps what it made.
        /// </summary>
        public abstract void Run(in Frame frame, RenderGraph graph);

        /// <summary>
        /// Builds, one time, how each frame takes its input from the outputs of <paramref name="makers"/>.
        /// </summary>
        public abstract void Bind(IReadOnlyDictionary<Type, Maker> makers);

        /// <summary>
        /// Builds, once, how the <see cref="RenderViews"/> it made at its last run is read: the item of a tuple, or the
        /// whole when <paramref name="item"/> is null.
        /// </summary>
        public abstract Func<RenderViews?> CreateViewsReader(string? item);
    }

    /// <summary>
    /// A step whose input is built each frame from its makers' typed outputs.
    /// </summary>
    /// <remarks>
    /// A compiled constructor call builds the input, so a frame does not box or reflect. Each render form is called as
    /// <see cref="IRender{TInput, TOutput}"/>, with <see cref="None"/> for what it lacks.
    /// </remarks>
    private sealed class Step<TInput, TOutput>(object part) : Step(part)
        where TInput : struct
        where TOutput : struct
    {
        /// <summary>
        /// The input record's constructor with the most parameters, one for each input; none for <see cref="None"/>.
        /// </summary>
        private static readonly ConstructorInfo? _constructor = typeof(TInput)
            .GetConstructors()
            .MaxBy(static constructor => constructor.GetParameters().Length);

        private readonly Renders _render = part switch
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
            _ => throw new InvalidOperationException(
                $"{GemRegistry.GetGemName(part.GetType())}'s {part.GetType().Name} has no render form."
            ),
        };

        private Func<TInput?> _take = static () => default(TInput);

        /// <summary>
        /// What it made at its last run; null when it made nothing, wasn't run or threw.
        /// </summary>
        public TOutput? Made { get; private set; }

        public override bool HasMade => Made.HasValue;

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
        /// Builds <c>input = madeA.HasValue &amp;&amp; ... ? new TInput(madeA.Value, madeB, ...) : null</c>.
        /// </summary>
        /// <remarks>
        /// A tuple item is taken as <c>madeA.Value.Item1</c>. A nullable input with no maker is passed as null.
        /// </remarks>
        public override void Bind(IReadOnlyDictionary<Type, Maker> makers)
        {
            Made = null;
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
                    if (Missing is null && parameter.ParameterType == type)
                    {
                        Missing = type;
                        Failure = ($"{Gem} lacks an input", $"It takes {type.Name}, which no listed gem makes.");
                    }

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

        public override Func<RenderViews?> CreateViewsReader(string? item)
        {
            Expression made = Expression.Property(Expression.Constant(this), nameof(Made));
            Expression value = Expression.Property(made, nameof(Nullable<>.Value));
            if (item is not null)
                value = Expression.Field(value, item);

            return Expression
                .Lambda<Func<RenderViews?>>(
                    Expression.Condition(
                        Expression.Property(made, nameof(Nullable<>.HasValue)),
                        Expression.Convert(value, typeof(RenderViews?)),
                        Expression.Default(typeof(RenderViews?))
                    )
                )
                .Compile();
        }

        /// <summary>
        /// The gem's render form, called as <c>Render</c> of <see cref="IRender{TInput, TOutput}"/>.
        /// </summary>
        private delegate TOutput? Renders(in Frame frame, RenderGraph graph, in TInput input);
    }
}
