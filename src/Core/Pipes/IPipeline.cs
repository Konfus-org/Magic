namespace Magic.Pipelines;

/// <summary>
/// A pipeline over <typeparamref name="TState"/>, the type of its state: given the state as what ran before it left it,
/// it does its work and hands the state on, changed or not. A pipeline may run others nested inside it. A gem joins the
/// pipeline over a state by implementing it, and runs there after the gems it needs, then in the order the project lists
/// them. One run now, such as a change list's, returns finished, which <see cref="ValueTask.FromResult{T}"/> does without
/// allocating. One that is awaited, such as the world's stages, may await.
/// </summary>
public interface IPipeline<TState>
{
    ValueTask<TState> RunAsync(TState state, CancellationToken cancel);
}
