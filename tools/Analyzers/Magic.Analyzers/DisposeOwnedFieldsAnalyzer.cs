using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Magic.Analyzers;

/// <summary>
/// MG0003: a disposable class disposes each disposable field or property that it makes (CODE_STANDARDS Classes).
/// </summary>
/// <remarks>
/// <para>
/// A field or property is the class's own when the class assigns it a <c>new</c> object, also through
/// <c>Track(new ...)</c>. A member that the class gets from elsewhere is not checked, as CA2213 does not check it.
/// </para>
/// <para>
/// The field is disposed when the class passes it to <c>Track</c>, or calls <c>Dispose</c> or <c>DisposeAsync</c> on
/// it in a <c>Track</c> callback, in a <c>Dispose</c> or <c>DisposeAsync</c> method of the class, or in a method of
/// the class that one of these calls. <c>Track(_field.Dispose)</c>, <c>Track(Close)</c>, <c>_field?.Dispose()</c>
/// and <c>Interlocked.Exchange(ref _field, null)?.Dispose()</c> count. <see cref="DisposeOwnedFieldsCodeFix"/> wraps
/// the <c>new</c> in <c>Track</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DisposeOwnedFieldsAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "MG0003";

    public static readonly DiagnosticDescriptor Rule = new(
        Id,
        "A disposable field or property that the class makes is not disposed",
        "'{0}' of type '{1}' is made by this class but not disposed: {2}",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A class disposes what it makes. A class that derives from Magic.Utils.Disposable tracks it."
    );

    private const string TrackHint = "pass it to Track, or dispose it in a Track callback or in Dispose(bool)";

    private const string DisposeHint = "dispose it in Dispose or DisposeAsync";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? disposable = start.Compilation.GetTypeByMetadataName("System.IDisposable");
            if (disposable is null)
                return;

            var types = new KnownTypes(
                disposable,
                start.Compilation.GetTypeByMetadataName("System.IAsyncDisposable"),
                start.Compilation.GetTypeByMetadataName("Magic.Utils.Disposable"),
                start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"),
                start.Compilation.GetTypeByMetadataName("System.Threading.Interlocked")
            );
            start.RegisterSymbolStartAction(symbolStart => StartType(symbolStart, types), SymbolKind.NamedType);
        });
    }

    private static void StartType(SymbolStartAnalysisContext context, KnownTypes types)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || !types.IsDisposable(type))
            return;

        var state = new TypeState(type, types);
        context.RegisterOperationAction(
            operation => state.OnAssignment((IAssignmentOperation)operation.Operation),
            OperationKind.SimpleAssignment,
            OperationKind.CoalesceAssignment
        );
        context.RegisterOperationAction(
            operation => state.OnFieldInitializer((IFieldInitializerOperation)operation.Operation),
            OperationKind.FieldInitializer
        );
        context.RegisterOperationAction(
            operation => state.OnPropertyInitializer((IPropertyInitializerOperation)operation.Operation),
            OperationKind.PropertyInitializer
        );
        context.RegisterOperationAction(
            operation => state.OnInvocation((IInvocationOperation)operation.Operation, operation.ContainingSymbol),
            OperationKind.Invocation
        );
        context.RegisterSymbolEndAction(state.Report);
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    /// <summary>
    /// The types that the rule needs, from the compilation. Each but IDisposable can be missing.
    /// </summary>
    private sealed class KnownTypes(
        INamedTypeSymbol disposable,
        INamedTypeSymbol? asyncDisposable,
        INamedTypeSymbol? magicDisposable,
        INamedTypeSymbol? task,
        INamedTypeSymbol? interlocked
    )
    {
        public INamedTypeSymbol? MagicDisposable { get; } = magicDisposable;

        public INamedTypeSymbol? Interlocked { get; } = interlocked;

        public static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol baseType)
        {
            for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                    return true;
            }

            return false;
        }

        public bool IsDisposable(ITypeSymbol type) =>
            Implements(type, disposable) || (asyncDisposable is not null && Implements(type, asyncDisposable));

        public bool IsOwnedFieldType(ITypeSymbol type) =>
            IsDisposable(type) && (task is null || !DerivesFrom(type, task));

        public bool IsMagicDisposable(INamedTypeSymbol type) =>
            MagicDisposable is not null && DerivesFrom(type, MagicDisposable);

        private static bool Implements(ITypeSymbol type, INamedTypeSymbol @interface) =>
            SymbolEqualityComparer.Default.Equals(type, @interface)
            || type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, @interface));
    }

    /// <summary>
    /// Where a class makes the object of a field, and whether it is in the field's initializer.
    /// </summary>
    private sealed class Creation(Location location, bool inInitializer)
    {
        public Location Location { get; } = location;

        public bool InInitializer { get; } = inInitializer;
    }

    /// <summary>
    /// What one class makes, disposes and calls, gathered from its operations, then reported at its end.
    /// </summary>
    private sealed class TypeState(INamedTypeSymbol type, KnownTypes types)
    {
        private readonly object _gate = new();

        private readonly Dictionary<ISymbol, Creation> _owned = new(SymbolEqualityComparer.Default);

        private readonly HashSet<ISymbol> _tracked = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<ISymbol, HashSet<ISymbol>> _disposedIn = new(SymbolEqualityComparer.Default);

        private readonly Dictionary<ISymbol, HashSet<ISymbol>> _calls = new(SymbolEqualityComparer.Default);

        private readonly HashSet<ISymbol> _trackCallbackCalls = new(SymbolEqualityComparer.Default);

        private readonly bool _isMagicDisposable = types.IsMagicDisposable(type);

        public void OnAssignment(IAssignmentOperation assignment)
        {
            if (OwnField(Unwrap(assignment.Target)) is ISymbol field)
                OnValue(field, assignment.Value, inInitializer: false);
        }

        public void OnFieldInitializer(IFieldInitializerOperation initializer)
        {
            foreach (ISymbol field in initializer.InitializedFields)
            {
                if (OwnMember(field) is ISymbol own)
                    OnValue(own, initializer.Value, inInitializer: true);
            }
        }

        public void OnPropertyInitializer(IPropertyInitializerOperation initializer)
        {
            foreach (ISymbol property in initializer.InitializedProperties)
            {
                if (OwnMember(property) is ISymbol own)
                    OnValue(own, initializer.Value, inInitializer: true);
            }
        }

        public void OnInvocation(IInvocationOperation invocation, ISymbol containingSymbol)
        {
            IMethodSymbol method = invocation.TargetMethod;
            if (IsTrack(method))
            {
                IOperation? argument = invocation.Arguments.Length == 1 ? Unwrap(invocation.Arguments[0].Value) : null;
                if (argument is IAssignmentOperation assignment)
                    argument = Unwrap(assignment.Target);

                if (argument is IDelegateCreationOperation { Target: IMethodReferenceOperation reference })
                {
                    OnTrackedMethod(reference);
                    return;
                }

                if (OwnField(argument) is ISymbol trackedField)
                {
                    lock (_gate)
                        _tracked.Add(trackedField);
                }

                return;
            }

            bool inTrackCallback = InTrackCallback(invocation);
            if (method.Name is "Dispose" or "DisposeAsync" && OwnField(DisposedValue(invocation)) is ISymbol field)
            {
                lock (_gate)
                {
                    if (inTrackCallback)
                        _tracked.Add(field);
                    else
                        Add(_disposedIn, containingSymbol, field);
                }

                return;
            }

            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, type))
                return;

            lock (_gate)
            {
                if (inTrackCallback)
                    _trackCallbackCalls.Add(method.OriginalDefinition);
                else
                    Add(_calls, containingSymbol, method.OriginalDefinition);
            }
        }

        public void Report(SymbolAnalysisContext context)
        {
            lock (_gate)
            {
                var reached = new HashSet<ISymbol>(_trackCallbackCalls, SymbolEqualityComparer.Default);
                foreach (ISymbol member in type.GetMembers())
                {
                    if (member is IMethodSymbol method && IsDisposeMethod(method))
                        reached.Add(member);
                }

                var pending = new Stack<ISymbol>(reached);
                while (pending.Count > 0)
                {
                    if (!_calls.TryGetValue(pending.Pop(), out HashSet<ISymbol>? callees))
                        continue;

                    foreach (ISymbol callee in callees)
                    {
                        if (reached.Add(callee))
                            pending.Push(callee);
                    }
                }

                var disposed = new HashSet<ISymbol>(_tracked, SymbolEqualityComparer.Default);
                foreach (ISymbol method in reached)
                {
                    if (_disposedIn.TryGetValue(method, out HashSet<ISymbol>? fields))
                        disposed.UnionWith(fields);
                }

                foreach (KeyValuePair<ISymbol, Creation> owned in _owned)
                {
                    if (disposed.Contains(owned.Key))
                        continue;

                    ImmutableArray<Location> creation =
                        _isMagicDisposable && !owned.Value.InInitializer ? [owned.Value.Location] : [];
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Rule,
                            owned.Key.Locations.FirstOrDefault(),
                            creation,
                            owned.Key.Name,
                            MemberType(owned.Key).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            _isMagicDisposable ? TrackHint : DisposeHint
                        )
                    );
                }
            }
        }

        private static ITypeSymbol MemberType(ISymbol member) =>
            member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

        private static bool IsDisposeMethod(IMethodSymbol method) =>
            method.Name is "Dispose" or "DisposeAsync"
            || method.ExplicitInterfaceImplementations.Any(@interface =>
                @interface.Name is "Dispose" or "DisposeAsync"
            );

        private static void Add<T>(Dictionary<ISymbol, HashSet<T>> map, ISymbol key, T value)
            where T : class, ISymbol
        {
            if (!map.TryGetValue(key, out HashSet<T>? values))
            {
                values = new HashSet<T>(SymbolEqualityComparer.Default);
                map[key] = values;
            }

            values.Add(value);
        }

        /// <summary>
        /// Records that <paramref name="field"/> is the class's own when <paramref name="value"/> makes a new object,
        /// and that it is tracked when the value is <c>Track(new ...)</c>.
        /// </summary>
        private void OnValue(ISymbol field, IOperation value, bool inInitializer)
        {
            value = Unwrap(value) ?? value;
            bool tracked = false;
            if (value is IInvocationOperation invocation && IsTrack(invocation.TargetMethod))
            {
                tracked = true;
                value = invocation.Arguments.Length == 1 ? Unwrap(invocation.Arguments[0].Value) ?? value : value;
            }

            if (value is not (IObjectCreationOperation or ITypeParameterObjectCreationOperation))
                return;

            lock (_gate)
            {
                if (!_owned.ContainsKey(field))
                    _owned[field] = new Creation(value.Syntax.GetLocation(), inInitializer);

                if (tracked)
                    _tracked.Add(field);
            }
        }

        /// <summary>
        /// Records a method group passed to <c>Track</c>: <c>Track(_field.Dispose)</c> tracks the field, and
        /// <c>Track(Close)</c> makes a method of the class a callback.
        /// </summary>
        private void OnTrackedMethod(IMethodReferenceOperation reference)
        {
            IMethodSymbol method = reference.Method;
            lock (_gate)
            {
                if (method.Name is "Dispose" or "DisposeAsync" && OwnField(Unwrap(reference.Instance)) is ISymbol field)
                    _tracked.Add(field);
                else if (SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, type))
                    _trackCallbackCalls.Add(method.OriginalDefinition);
            }
        }

        private ISymbol? OwnField(IOperation? operation) =>
            operation switch
            {
                IFieldReferenceOperation reference => OwnMember(reference.Field),
                IPropertyReferenceOperation reference => OwnMember(reference.Property),
                _ => null,
            };

        private ISymbol? OwnMember(ISymbol member)
        {
            ITypeSymbol? memberType = member switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol { IsIndexer: false } property => property.Type,
                _ => null,
            };

            return
                memberType is not null
                && !member.IsStatic
                && SymbolEqualityComparer.Default.Equals(member.ContainingType.OriginalDefinition, type)
                && types.IsOwnedFieldType(memberType)
                ? member.OriginalDefinition
                : null;
        }

        private bool IsTrack(IMethodSymbol method) =>
            method.Name == "Track"
            && types.MagicDisposable is not null
            && SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, types.MagicDisposable);

        private bool InTrackCallback(IOperation operation)
        {
            for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
            {
                if (current is not IAnonymousFunctionOperation)
                    continue;

                IOperation? outer = current.Parent;
                while (outer is IDelegateCreationOperation or IConversionOperation)
                    outer = outer.Parent;

                if (
                    outer is IArgumentOperation { Parent: IInvocationOperation invocation }
                    && IsTrack(invocation.TargetMethod)
                )
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the value that a Dispose call disposes: the instance, through <c>?.</c> and
        /// <c>Interlocked.Exchange(ref field, ...)</c>.
        /// </summary>
        private IOperation? DisposedValue(IInvocationOperation invocation)
        {
            IOperation? instance = Unwrap(invocation.Instance);
            if (instance is IConditionalAccessInstanceOperation)
            {
                IOperation? current = instance.Parent;
                while (current is not null and not IConditionalAccessOperation)
                    current = current.Parent;

                instance = Unwrap((current as IConditionalAccessOperation)?.Operation);
            }

            if (
                instance is IInvocationOperation { TargetMethod.Name: "Exchange" } exchange
                && types.Interlocked is not null
                && SymbolEqualityComparer.Default.Equals(exchange.TargetMethod.ContainingType, types.Interlocked)
                && exchange.Arguments.Length > 0
            )
            {
                instance = Unwrap(exchange.Arguments[0].Value);
            }

            return instance;
        }
    }
}
