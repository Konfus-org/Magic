using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// Times a frame on the GPU's own clock, while something listens to either instrument: a timestamp before and after
/// each top-level debug group, a renderer's pass, and the uploads, and the frame from its first command to its last.
/// The times are read when the frame's slot comes round, the GPU done with it by then, so nothing waits for them, and
/// writing them changes nothing about how the frame runs, so a pass's time is its time in the frame as it is.
/// </summary>
internal sealed unsafe class VulkanTimer : IDisposable
{
    /// <summary>
    /// What a frame's uploads are timed as.
    /// </summary>
    internal const string UploadsGroup = "(uploads)";

    /// <summary>
    /// Timestamps a frame can write: the frame's two, then two a group; past them, groups go untimed.
    /// </summary>
    private const uint MaxQueries = 1024;

    private const string GroupTag = "magic.gems.vulkan_gpu.group";

    private readonly VulkanDevice _device;
    private readonly Histogram<double> _groupDuration;
    private readonly Histogram<double> _frameDuration;
    private readonly VkQueryPool[] _pools;
    private readonly string[][] _groups;
    private readonly uint[] _counts;
    private readonly bool[] _isTimed;
    private readonly ulong[] _ticks = new ulong[MaxQueries];

    /// <summary>
    /// Group names as strings, made once each, so a frame's names allocate nothing.
    /// </summary>
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>
    /// Each group's time this frame, added up where a name recurs (a pass per view).
    /// </summary>
    private readonly Dictionary<string, double> _groupSec = new(StringComparer.Ordinal);

    private readonly double _secondsPerTick;
    private readonly ulong _mask;
    private int _slot;

    public VulkanTimer(
        VulkanDevice device,
        Histogram<double> groupDuration,
        Histogram<double> frameDuration,
        int framesInFlight)
    {
        _device = device;
        _groupDuration = groupDuration;
        _frameDuration = frameDuration;
        _secondsPerTick = device.Limits.timestampPeriod * 1e-9;
        _mask = device.TimestampBits >= 64 ? ulong.MaxValue : (1ul << (int)device.TimestampBits) - 1;
        _pools = new VkQueryPool[framesInFlight];
        _groups = new string[framesInFlight][];
        _counts = new uint[framesInFlight];
        _isTimed = new bool[framesInFlight];
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            VkQueryPoolCreateInfo info = new() { queryType = VkQueryType.Timestamp, queryCount = MaxQueries };
            VkQueryPool pool;
            device.Api.vkCreateQueryPool(&info, &pool);
            device.Api.vkResetQueryPool(pool, 0, MaxQueries);
            _pools[slot] = pool;
            _groups[slot] = new string[(MaxQueries / 2) - 1];
        }
    }

    /// <summary>
    /// Whether frames are timed: something listens, and the queue writes timestamps.
    /// </summary>
    internal bool IsListenedTo => (_groupDuration.Enabled || _frameDuration.Enabled) && _device.TimestampBits > 0;

    public void Dispose()
    {
        foreach (VkQueryPool pool in _pools)
        {
            _device.Api.vkDestroyQueryPool(pool);
        }
    }

    /// <summary>
    /// Times the frame about to be recorded from <paramref name="slot"/>, whose last frame was collected.
    /// </summary>
    internal void Begin(VkCommandBuffer commandBuffer, int slot)
    {
        _slot = slot;
        _isTimed[slot] = true;
        _counts[slot] = 0;
        _device.Api.vkCmdWriteTimestamp2(commandBuffer, VkPipelineStageFlags2.TopOfPipe, _pools[slot], 0);
    }

    internal void End(VkCommandBuffer commandBuffer)
        => _device.Api.vkCmdWriteTimestamp2(commandBuffer, VkPipelineStageFlags2.BottomOfPipe, _pools[_slot], 1);

    /// <summary>
    /// Starts timing a group named <paramref name="group"/>; false when the frame has no room left for it, which
    /// <see cref="Stop"/> is then not called for.
    /// </summary>
    internal bool Start(VkCommandBuffer commandBuffer, string group)
    {
        uint count = _counts[_slot];

        if (count >= _groups[_slot].Length) return false;

        _groups[_slot][count] = group;
        _device.Api
            .vkCmdWriteTimestamp2(commandBuffer, VkPipelineStageFlags2.TopOfPipe, _pools[_slot], 2 + (count * 2));

        return true;
    }

    internal void Stop(VkCommandBuffer commandBuffer)
    {
        uint count = _counts[_slot]++;
        _device.Api.vkCmdWriteTimestamp2(
            commandBuffer,
            VkPipelineStageFlags2.BottomOfPipe,
            _pools[_slot],
            3 + (count * 2));
    }

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
    /// Records the times of the frame last recorded from <paramref name="slot"/>, which the GPU has finished, and makes
    /// its timestamps ready to write again.
    /// </summary>
    internal void Collect(int slot)
    {
        if (!_isTimed[slot]) return;

        _isTimed[slot] = false;
        uint queries = 2 + (_counts[slot] * 2);
        VkResult result;
        fixed (ulong* ticks = _ticks)
        {
            result = _device.Api.vkGetQueryPoolResults(
                _pools[slot],
                0,
                queries,
                queries * sizeof(ulong),
                ticks,
                sizeof(ulong),
                VkQueryResultFlags.Bit64);
        }

        _device.Api.vkResetQueryPool(_pools[slot], 0, queries);

        if (result != VkResult.Success) return;

        for (int i = 0; i < _counts[slot]; i++)
        {
            ref double groupSec = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _groupSec,
                _groups[slot][i],
                out _);
            groupSec += ToSeconds(_ticks[2 + (i * 2)], _ticks[3 + (i * 2)]);
        }

        foreach ((string group, double groupSec) in _groupSec)
        {
            _groupDuration.Record(groupSec, new KeyValuePair<string, object?>(GroupTag, group));
        }

        _frameDuration.Record(ToSeconds(_ticks[0], _ticks[1]));
        _groupSec.Clear();
    }

    private double ToSeconds(ulong startTimestamp, ulong endTimestamp)
        => ((endTimestamp - startTimestamp) & _mask) * _secondsPerTick;
}
