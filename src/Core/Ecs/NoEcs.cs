namespace Magic.Ecs;

/// <summary>
/// What the world has when no gem provides the ECS: every call says so.
/// </summary>
internal sealed class NoEcs : IEcs
{
    public static InvalidOperationException GetMissing() =>
        new("No gem the project lists provides the ECS, so the world holds no entities.");

    public Entity Create() => throw GetMissing();

    public Entity Create(ReadOnlySpan<IComponent> components) => throw GetMissing();

    public void Create<T1>(ReadOnlySpan<T1> first, Span<Entity> created)
        where T1 : unmanaged, IComponent => throw GetMissing();

    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, Span<Entity> created)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => throw GetMissing();

    public void Destroy(Entity entity) => throw GetMissing();

    public bool IsAlive(Entity entity) => throw GetMissing();

    public IComponent[] GetComponents(Entity entity) => throw GetMissing();

    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent => throw GetMissing();

    public void Set(Entity entity, ReadOnlySpan<IComponent> components) => throw GetMissing();

    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent => throw GetMissing();

    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => throw GetMissing();

    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent => throw GetMissing();

    public EcsQuery Query() => throw GetMissing();

    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => throw GetMissing();

    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => throw GetMissing();

    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => throw GetMissing();

    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => throw GetMissing();

    public void Run<TState>(in TState state, EcsChunkRun<TState, EcsChunk> run) => throw GetMissing();

    public void Run<TState, T1>(in TState state, EcsChunkRun<TState, EcsChunk<T1>> run)
        where T1 : unmanaged, IComponent => throw GetMissing();

    public void Run<TState, T1, T2>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => throw GetMissing();

    public void Run<TState, T1, T2, T3>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => throw GetMissing();

    public void Run<TState, T1, T2, T3, T4>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3, T4>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => throw GetMissing();
}
