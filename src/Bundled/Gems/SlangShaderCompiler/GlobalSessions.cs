using System.Collections.Concurrent;
using Magic.Contexts.Utils;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// Slang's global sessions, a pool: one is used by one thread at a time, and starting one loads Slang's core module, so
/// a compile takes one no other compile holds and gives it back when done, for the next to take.
/// </summary>
internal sealed unsafe class GlobalSessions : IDisposable
{
    private readonly ConcurrentStack<nint> _idle = new();
    private volatile bool _isDisposed;

    /// <summary>
    /// Releases the idle sessions; one given back after this is released as it comes back.
    /// </summary>
    public void Dispose()
    {
        _isDisposed = true;
        ReleaseIdle();
    }

    internal Result<nint> Take()
    {
        if (_idle.TryPop(out nint session)) return Result.Success(session);

        Slang.GlobalSessionDesc desc = new()
        {
            StructureSize = (uint)sizeof(Slang.GlobalSessionDesc),
            MinLanguageVersion = Slang.LanguageVersion2025,
        };
        int created = Slang.slang_createGlobalSession2(&desc, &session);

        return created < 0
            ? Result.Failure<nint>($"Slang could not start (0x{created:X8}).")
            : Result.Success(session);
    }

    internal void Return(nint session)
    {
        _idle.Push(session);
        if (_isDisposed) ReleaseIdle();
    }

    private void ReleaseIdle()
    {
        while (_idle.TryPop(out nint session))
        {
            Slang.Release(session);
        }
    }
}
