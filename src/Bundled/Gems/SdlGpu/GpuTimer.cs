using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text;
using Magic.SdlBindings;

namespace Magic.SdlGpu;

/// <summary>
/// Times stretches of a frame on the GPU, which SDL's GPU API has no timestamp queries for: while either instrument is
/// listened to, the frame is submitted as a command buffer per stretch, each with a fence, and a thread of the timer's
/// own spins on the fences in submit order, stamping the moment each signals. A stretch's time runs from the later of
/// the stamp before it and its own submit to its stamp, so a GPU waiting for the CPU charges no stretch. The blocking
/// fence wait would hold SDL's submit lock on some backends, stalling the render thread, so the thread polls, and uses
/// a core only while something listens. The numbers come back to the render thread when the frame's slot is reused,
/// every fence of it signalled by then, so nothing waits on the GPU for them.
/// <para>
/// The render thread writes a slot's stretches and publishes their count; the timing thread writes their times and
/// then the slot's done flag. Neither writes what the other does.
/// </para>
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    /// <summary>
    /// Stretches a frame is cut into at most: past them, the rest of the frame is the last one.
    /// </summary>
    internal const int MaxStretches = 256;

    private const string GroupTag = "magic.gems.sdl_gpu.group";

    private readonly SDL.GPUDevice* _device;
    private readonly Histogram<double> _groupDuration;
    private readonly Histogram<double> _frameDuration;

    /// <summary>
    /// Released once for each frame begun, so the timing thread sleeps between frames rather than spinning.
    /// </summary>
    private readonly SemaphoreSlim _begun = new(0);

    /// <summary>
    /// The slots of the frames begun, in order, round a ring as long as the frames in flight: timing can start on any
    /// slot, and a frame is collected before its slot begins again, so no entry is overwritten before it is read.
    /// </summary>
    private readonly int[] _begunSlots;

    /// <summary>
    /// Group names as strings, made once each, so a frame's names allocate nothing.
    /// </summary>
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>
    /// Each group's time this frame, added up where a name recurs (a pass per view), in the order first submitted.
    /// </summary>
    private readonly Dictionary<string, long> _groupTicks = new(StringComparer.Ordinal);

    /// <summary>
    /// Per frame slot: the stretches submitted, how many are published, whether the frame is ended, and its timing
    /// done; the render thread's but for the times and the done flag.
    /// </summary>
    private readonly Stretch[][] _stretches;
    private readonly long[][] _ticks;
    private readonly int[] _counts;
    private readonly bool[] _isEnded;
    private readonly bool[] _isDone;
    private readonly bool[] _isTimed;

    private Thread? _thread;
    private volatile bool _isStopping;
    private int _slot;
    private uint _begunCount;

    public GpuTimer(
        SDL.GPUDevice* device,
        int framesInFlight,
        Histogram<double> groupDuration,
        Histogram<double> frameDuration)
    {
        _device = device;
        _groupDuration = groupDuration;
        _frameDuration = frameDuration;
        _stretches = new Stretch[framesInFlight][];
        _ticks = new long[framesInFlight][];
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _stretches[slot] = new Stretch[MaxStretches];
            _ticks[slot] = new long[MaxStretches];
        }

        _counts = new int[framesInFlight];
        _isEnded = new bool[framesInFlight];
        _isDone = new bool[framesInFlight];
        _isTimed = new bool[framesInFlight];
        _begunSlots = new int[framesInFlight];
    }

    /// <summary>
    /// Whether frames are timed: something listens to either instrument.
    /// </summary>
    internal bool IsListenedTo => _groupDuration.Enabled || _frameDuration.Enabled;

    /// <summary>
    /// How many stretches the frame begun last has submitted.
    /// </summary>
    internal int Count => _counts[_slot];

    /// <summary>
    /// Stops the timing thread. Every frame begun must be collected first, so it is not spinning on a fence.
    /// </summary>
    public void Dispose()
    {
        _isStopping = true;
        _begun.Release();
        _thread?.Join();
        _begun.Dispose();
    }

    /// <summary>
    /// Times the frame about to be submitted from <paramref name="slot"/>, whose last frame was collected.
    /// </summary>
    internal void Begin(int slot)
    {
        _slot = slot;
        _isTimed[slot] = true;
        _begunSlots[_begunCount++ % (uint)_begunSlots.Length] = slot;
        if (_thread is null)
        {
            _thread = new Thread(TimeFrames) { IsBackground = true, Name = "GPU timing" };
            _thread.Start();
        }

        _begun.Release();
    }

    /// <summary>
    /// Submits a stretch of the frame named <paramref name="group"/>; its fence is the timer's until the frame is
    /// collected. The frame's last stretch's fence is the frame's own.
    /// </summary>
    internal SDL.GPUFence* Submit(SDL.GPUCommandBuffer* commandBuffer, string group)
    {
        long submitted = Stopwatch.GetTimestamp();
        SDL.GPUFence* fence = SDL.SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
        int count = _counts[_slot];
        _stretches[_slot][count] = new Stretch((nint)fence, group, submitted);
        Volatile.Write(ref _counts[_slot], count + 1);
        return fence;
    }

    /// <summary>
    /// Ends the frame begun last: no stretch follows.
    /// </summary>
    internal void End() => Volatile.Write(ref _isEnded[_slot], true);

    /// <summary>
    /// The name a debug group's UTF-8 bytes say, as a string made the first time it is seen.
    /// </summary>
    internal string GetName(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[Encoding.UTF8.GetMaxCharCount(utf8.Length)];
        chars = chars[..Encoding.UTF8.GetChars(utf8, chars)];
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(chars, out string? name)) return name;

        name = chars.ToString();
        _names.Add(name);
        return name;
    }

    /// <summary>
    /// Records the times of the frame last submitted from <paramref name="slot"/>, once the GPU has finished it, and
    /// releases its fences, the frame's own among them. False when it was not timed, so its fence is the caller's.
    /// </summary>
    internal bool TryCollect(int slot)
    {
        if (!_isTimed[slot]) return false;

        // Every fence of the frame has signalled, so the timing thread is done with it within moments.
        SpinWait spin = default;
        while (!Volatile.Read(ref _isDone[slot]))
        {
            spin.SpinOnce(sleep1Threshold: -1);
        }

        long frameTicks = 0;
        for (int i = 0; i < _counts[slot]; i++)
        {
            Stretch stretch = _stretches[slot][i];
            if (stretch.Fence != 0) SDL.ReleaseGPUFence(_device, (SDL.GPUFence*)stretch.Fence);

            frameTicks += _ticks[slot][i];
            CollectionsMarshal.GetValueRefOrAddDefault(_groupTicks, stretch.Group, out _) += _ticks[slot][i];
        }

        foreach ((string group, long ticks) in _groupTicks)
        {
            _groupDuration.Record(
                (double)ticks / Stopwatch.Frequency,
                new KeyValuePair<string, object?>(GroupTag, group));
        }

        _frameDuration.Record((double)frameTicks / Stopwatch.Frequency);
        _groupTicks.Clear();
        _counts[slot] = 0;
        _isEnded[slot] = false;
        _isDone[slot] = false;
        _isTimed[slot] = false;
        return true;
    }

    /// <summary>
    /// The timing thread: each frame begun, in order, its stretches' fences polled in submit order until the frame
    /// has ended and every stretch it submitted has signalled.
    /// </summary>
    private void TimeFrames()
    {
        uint begun = 0;
        long lastEnd = 0;
        while (true)
        {
            _begun.Wait();
            if (_isStopping) return;

            int slot = _begunSlots[begun++ % (uint)_begunSlots.Length];
            lastEnd = TimeFrame(slot, lastEnd);
            Volatile.Write(ref _isDone[slot], true);
        }
    }

    /// <summary>
    /// Times one frame's stretches, the frame before it having ended at <paramref name="lastEnd"/>, and returns when
    /// its last stretch did.
    /// </summary>
    private long TimeFrame(int slot, long lastEnd)
    {
        SpinWait spin = default;
        int index = 0;
        while (true)
        {
            if (index == Volatile.Read(ref _counts[slot]))
            {
                // Ended is written after the last count, so once it reads true, the count read after it is final.
                if (Volatile.Read(ref _isEnded[slot]) && index == Volatile.Read(ref _counts[slot])) return lastEnd;

                spin.SpinOnce(sleep1Threshold: -1);
                continue;
            }

            Stretch stretch = _stretches[slot][index];
            if (stretch.Fence == 0)
            {
                _ticks[slot][index++] = 0;
                continue;
            }

            if (!SDL.QueryGPUFence(_device, (SDL.GPUFence*)stretch.Fence))
            {
                spin.SpinOnce(sleep1Threshold: -1);
                continue;
            }

            long end = Stopwatch.GetTimestamp();
            _ticks[slot][index++] = end - Math.Max(lastEnd, stretch.Submitted);
            lastEnd = end;
            spin.Reset();
        }
    }

    /// <summary>
    /// A stretch submitted: its fence, none when the submit failed; the debug group it is named for; and when it was
    /// submitted, in <see cref="Stopwatch"/> ticks.
    /// </summary>
    private readonly record struct Stretch(nint Fence, string Group, long Submitted);
}
