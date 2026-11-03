using System.Runtime.CompilerServices;

namespace Magic.Threading;

/// <summary>
/// What <see cref="Threads.Enter"/> awaits.
/// </summary>
public readonly record struct ThreadEntry(Threads Threads, EngineThread Thread) : INotifyCompletion
{
    public bool IsCompleted => Threads.IsCurrent(Thread);

    public ThreadEntry GetAwaiter() => this;

    public void GetResult() { }

    public void OnCompleted(Action continuation) => Threads.Post(Thread, continuation);
}
