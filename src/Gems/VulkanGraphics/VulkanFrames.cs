using System.Diagnostics;
using System.Diagnostics.Metrics;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// What a frame in flight owns: its command buffer, the fence that says the GPU is done with it, its staging ring,
/// its timestamps, what it threw away and what it promised to read back. Two of them take turns, so the CPU records
/// frame n + 1 while the GPU draws frame n and never waits for more than one.
/// </summary>
internal sealed unsafe class VulkanFrames : IDisposable
{
    public const int InFlight = 2;

    private const int TimestampCapacity = 512;

    /// <summary>
    /// A staging ring's size while frames upload only what a normal frame does.
    /// </summary>
    private const long StagingBytes = 8L * 1024 * 1024;

    /// <summary>
    /// How long a grown staging ring is kept after the last frame that needed more than <see cref="StagingBytes"/>,
    /// so a load that uploads every few frames doesn't make it again each time.
    /// </summary>
    private const double StagingKeptSec = 2;
    private const string PassTag = "magic.gems.vulkan_graphics.gpu.pass";

    private readonly VulkanDevice _device;
    private readonly VulkanResources _resources;
    private readonly Histogram<double> _passDuration;
    private readonly Histogram<double> _frameDuration;
    private readonly Frame[] _frames = new Frame[InFlight];
    private readonly List<Readback> _readbacks = [];
    private int _current;
    private ulong _serial;
    private ulong _completedSerial;

    /// <summary>
    /// When a frame last needed more than <see cref="StagingBytes"/> (<see cref="Stopwatch.GetTimestamp"/>).
    /// </summary>
    private long _stagingNeededTimestamp;

    public VulkanFrames(VulkanDevice device, VulkanResources resources, Meter meter)
    {
        _device = device;
        _resources = resources;
        _passDuration = meter.CreateHistogram<double>(
            "magic.gems.vulkan_graphics.gpu.pass.duration",
            "s",
            "One pass of a frame on the GPU, tagged with its name."
        );
        _frameDuration = meter.CreateHistogram<double>(
            "magic.gems.vulkan_graphics.gpu.frame.duration",
            "s",
            "A frame on the GPU, from its first pass's start to its last's end."
        );
        for (int i = 0; i < InFlight; i++)
        {
            _frames[i] = new Frame(device, resources, i);
        }
    }

    public Frame Current => _frames[_current];

    /// <summary>
    /// The bottom-level structures built in frames the GPU has finished, each with the size it compacts to, for the
    /// next frame to compact.
    /// </summary>
    public List<(VulkanAcceleration Built, ulong Bytes)> Compactable { get; } = [];

    public void Dispose()
    {
        _device.Api.vkDeviceWaitIdle();
        foreach (Frame frame in _frames)
        {
            Complete(frame);
            frame.Dispose();
        }

        foreach (Readback readback in _readbacks)
        {
            readback.Promise.TrySetResult(Result.Failure("the GPU was shut down first."));
        }
    }

    /// <summary>
    /// Moves to the next frame: waits for the GPU to finish what that frame last held, frees its trash, finishes its
    /// readbacks and reads its timestamps, then readies it to record. Render thread.
    /// </summary>
    public void Begin()
    {
        _current = (_current + 1) % InFlight;
        Frame frame = _frames[_current];
        if (frame.Serial > 0)
        {
            VkResult waited = _device.Api.vkWaitForFences(frame.Fence, true, ulong.MaxValue);
            if (waited != VkResult.Success)
                Log.Error($"Waiting for frame {frame.Serial} failed: {waited}.");

            Complete(frame);
        }

        // The GPU is done with the ring, so a grown one no frame has needed for a while is freed at once.
        if (
            frame.Staging.Description.Bytes > StagingBytes
            && Stopwatch.GetElapsedTime(_stagingNeededTimestamp).TotalSeconds > StagingKeptSec
        )
        {
            Trash grown = new(frame.StagingId, frame.Staging);
            if (frame.Restage(StagingBytes).Ok)
                _resources.Free(grown);
        }

        _device.Api.vkResetFences(frame.Fence);
        _device.Api.vkResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);
        frame.Serial = ++_serial;
        frame.StagingOffset = 0;
        VkCommandBufferBeginInfo beginInfo = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _device.Api.vkBeginCommandBuffer(frame.CommandBuffer, &beginInfo);
        _device.Api.vkCmdResetQueryPool(frame.CommandBuffer, frame.Queries, 0, TimestampCapacity);
    }

    /// <summary>
    /// Finishes every frame the GPU is done with, without waiting: what readbacks and trash wait for while no frame
    /// begins, as during the awaited start of a run. Render thread.
    /// </summary>
    public void Poll()
    {
        foreach (Frame frame in _frames)
        {
            if (frame.Serial == 0)
                continue;

            if (_device.Api.vkGetFenceStatus(frame.Fence) == VkResult.Success)
                Complete(frame);
        }
    }

    /// <summary>
    /// Room in the current frame's staging ring, grown when a frame needs more than it had. A grown ring shrinks back
    /// in <see cref="Begin"/> once no frame has needed more than <see cref="StagingBytes"/> for a while. <c>default</c>
    /// when the ring cannot grow; the render graph tells that failure.
    /// </summary>
    public GpuUpload Upload(int bytes, int alignment)
    {
        Frame frame = Current;
        long offset = (frame.StagingOffset + alignment - 1) / alignment * alignment;
        if (offset + bytes > StagingBytes)
            _stagingNeededTimestamp = Stopwatch.GetTimestamp();

        if (offset + bytes > frame.Staging.Description.Bytes)
        {
            // The old ring stays in the heap until the frame has run: the copies recorded so far name it by its id
            // when the frame's commands are translated at submit.
            Trash old = new(frame.StagingId, frame.Staging);
            if (!frame.Restage(Math.Max(frame.Staging.Description.Bytes * 2, offset + bytes)).Ok)
                return default;

            frame.Trash.Add(old);
            offset = 0;
        }

        frame.StagingOffset = offset + bytes;

        return new GpuUpload(
            new GpuBuffer(frame.StagingId),
            (ulong)offset,
            new Span<byte>(frame.Staging.Mapped + offset, bytes)
        );
    }

    /// <summary>
    /// Promises <paramref name="bytes"/> of <paramref name="buffer"/> once the frames submitted so far are done.
    /// Render thread.
    /// </summary>
    public Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(uint id, VulkanBuffer buffer, long offset, int bytes)
    {
        TaskCompletionSource<Result<ReadOnlyMemory<byte>>> promise = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (buffer.Mapped == null || !buffer.Description.Usage.HasFlag(BufferUsages.Readback))
        {
            promise.SetResult(Result.Failure($"buffer {id} is not a readback buffer."));
        }
        else if (offset < 0 || bytes < 0 || offset + bytes > buffer.Description.Bytes)
        {
            promise.SetResult(Result.Failure($"{bytes} bytes at {offset} are outside buffer {id}."));
        }
        else
        {
            // Done once the frame being recorded now has finished: it is the last that could write the buffer.
            _readbacks.Add(new Readback(promise, buffer, offset, bytes, Current.Serial));
        }

        return promise.Task;
    }

    /// <summary>
    /// Finishes what the GPU is done with: the readbacks up to its serial, then the frame's trash, so a buffer
    /// destroyed after its readback was promised is read before it is freed, and its timestamps.
    /// </summary>
    private void Complete(Frame frame)
    {
        _completedSerial = Math.Max(_completedSerial, frame.Serial);
        frame.Serial = 0;
        for (int i = _readbacks.Count - 1; i >= 0; i--)
        {
            Readback readback = _readbacks[i];
            if (readback.Serial > _completedSerial)
                continue;

            byte[] bytes = new byte[readback.Bytes];
            new ReadOnlySpan<byte>(readback.Buffer.Mapped + readback.Offset, readback.Bytes).CopyTo(bytes);
            readback.Promise.TrySetResult(Result.Success<ReadOnlyMemory<byte>>(bytes));
            _readbacks.RemoveAt(i);
        }

        frame.ReadCompactedSizes(Compactable);
        foreach (Trash trash in frame.Trash)
        {
            _resources.Free(trash);
        }

        frame.Trash.Clear();

        // Read back only while something listens to the meter.
        if (_passDuration.Enabled)
            frame.RecordTimings(_frameDuration, _passDuration);

        frame.Timestamps.Clear();
    }

    /// <summary>
    /// A resource freed by a frame, to die once the frame has run: a <see cref="VulkanBuffer"/>,
    /// <see cref="VulkanTexture"/>, <see cref="VkSampler"/>, <see cref="VulkanPipeline"/> or
    /// <see cref="VulkanAcceleration"/>, with the id it held.
    /// </summary>
    public readonly record struct Trash(uint Id, object Resource);

    /// <summary>
    /// A promise of a buffer's bytes once frame <paramref name="Serial"/> is done.
    /// </summary>
    private readonly record struct Readback(
        TaskCompletionSource<Result<ReadOnlyMemory<byte>>> Promise,
        VulkanBuffer Buffer,
        long Offset,
        int Bytes,
        ulong Serial
    );

    /// <summary>
    /// One frame's things.
    /// </summary>
    public sealed class Frame : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly VulkanResources _resources;

        public Frame(VulkanDevice device, VulkanResources resources, int index)
        {
            _device = device;
            _resources = resources;
            Index = index;
            device
                .Api.vkCreateCommandPool(VkCommandPoolCreateFlags.Transient, device.QueueFamily, out VkCommandPool pool)
                .ThrowIfFailed("a frame's command pool");
            Pool = pool;
            VkCommandBufferAllocateInfo allocateInfo = new()
            {
                commandPool = pool,
                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer commandBuffer;
            device
                .Api.vkAllocateCommandBuffers(&allocateInfo, &commandBuffer)
                .ThrowIfFailed("a frame's command buffer");
            CommandBuffer = commandBuffer;
            device.Api.vkCreateFence(VkFenceCreateFlags.Signaled, out VkFence fence).ThrowIfFailed("a frame's fence");
            Fence = fence;
            device.Api.vkCreateSemaphore(out VkSemaphore acquired).ThrowIfFailed("a frame's semaphore");
            Acquired = acquired;
            VkQueryPoolCreateInfo queryInfo = new()
            {
                queryType = VkQueryType.Timestamp,
                queryCount = TimestampCapacity,
            };
            device.Api.vkCreateQueryPool(&queryInfo, out VkQueryPool queries).ThrowIfFailed("a frame's query pool");
            Queries = queries;
            if (device.Features.HasRayQuery)
            {
                VkQueryPoolCreateInfo sizesInfo = new()
                {
                    queryType = VkQueryType.AccelerationStructureCompactedSizeKHR,
                    queryCount = VulkanHeap.Accelerations,
                };
                device
                    .Api.vkCreateQueryPool(&sizesInfo, out VkQueryPool sizes)
                    .ThrowIfFailed("a frame's compacted size queries");
                CompactedSizes = sizes;
            }

            Result staged = Restage(StagingBytes);
            if (Staging is null)
                throw new InvalidOperationException($"Frame {index} has no staging ring: {staged.Error}");
        }

        public int Index { get; }

        public VkCommandPool Pool { get; }

        public VkCommandBuffer CommandBuffer { get; }

        public VkFence Fence { get; }

        /// <summary>
        /// Signalled when the window's image is the frame's to draw into.
        /// </summary>
        public VkSemaphore Acquired { get; }

        public VkQueryPool Queries { get; }

        /// <summary>
        /// The size each bottom-level structure the frame built compacts to, by its index in <see cref="Built"/>; none
        /// on a GPU that traces no rays.
        /// </summary>
        public VkQueryPool CompactedSizes { get; }

        /// <summary>
        /// The bottom-level structures built in the frame, whose compacted sizes it queries.
        /// </summary>
        public List<VulkanAcceleration> Built { get; } = [];

        public VulkanBuffer Staging { get; private set; }

        public uint StagingId { get; private set; }

        public long StagingOffset { get; set; }

        /// <summary>
        /// The submission the GPU may still be running; 0 when none.
        /// </summary>
        public ulong Serial { get; set; }

        /// <summary>
        /// Resources freed while the frame recorded, which die once it has run.
        /// </summary>
        public List<Trash> Trash { get; } = [];

        /// <summary>
        /// The timestamps written, by query index: a name and which pass it belongs to.
        /// </summary>
        public List<(string Name, int Pass)> Timestamps { get; } = [];

        public void Dispose()
        {
            _device.Api.vkDestroyQueryPool(Queries);
            _device.Api.vkDestroyQueryPool(CompactedSizes);
            _device.Api.vkDestroySemaphore(Acquired);
            _device.Api.vkDestroyFence(Fence);
            _device.Api.vkDestroyCommandPool(Pool);
        }

        /// <summary>
        /// A new staging ring of <paramref name="bytes"/>, in the heap; the old one, if any, is the caller's to free. A
        /// ring that is not made keeps the old one, so only the first ring, which the constructor makes or throws, can
        /// be missing.
        /// </summary>
        public Result Restage(long bytes)
        {
            Result<GpuBuffer> made = _resources.CreateBuffer(
                new BufferDescription($"Staging {Index}", bytes, BufferUsages.Upload)
            );
            if (!made.Ok)
                return Result.Failure($"the staging ring of {bytes} bytes was not made: {made.Error}");

            StagingId = made.Value.Id;
            Staging =
                _resources.Buffers[StagingId] ?? throw new InvalidOperationException("The staging ring left the heap.");

            return Result.Success();
        }

        /// <summary>
        /// Writes the next timestamp, named; nothing when the frame has no room for more.
        /// </summary>
        public void WriteTimestamp(string name, int pass)
        {
            if (Timestamps.Count >= TimestampCapacity)
                return;

            uint query = (uint)Timestamps.Count;
            Timestamps.Add((name, pass));
            _device.Api.vkCmdWriteTimestamp2(CommandBuffer, VkPipelineStageFlags2.AllCommands, Queries, query);
        }

        /// <summary>
        /// Queries the size a bottom-level structure just built compacts to, after its build's barrier; nothing when
        /// the frame has no room for more, and the structure keeps its build's size.
        /// </summary>
        public void WriteCompactedSize(VulkanAcceleration built)
        {
            if (Built.Count >= VulkanHeap.Accelerations)
                return;

            uint query = (uint)Built.Count;
            Built.Add(built);
            VkAccelerationStructureKHR handle = built.Handle;
            _device.Api.vkCmdResetQueryPool(CommandBuffer, CompactedSizes, query, 1);
            _device.Api.vkCmdWriteAccelerationStructuresPropertiesKHR(
                CommandBuffer,
                1,
                &handle,
                VkQueryType.AccelerationStructureCompactedSizeKHR,
                CompactedSizes,
                query
            );
        }

        /// <summary>
        /// Adds each bottom-level structure the frame built, with the size it compacts to, to
        /// <paramref name="compactable"/>, once the frame has run.
        /// </summary>
        public void ReadCompactedSizes(List<(VulkanAcceleration Built, ulong Bytes)> compactable)
        {
            int count = Built.Count;
            if (count == 0)
                return;

            ulong* sizes = stackalloc ulong[count];
            VkResult read = _device.Api.vkGetQueryPoolResults(
                CompactedSizes,
                0,
                (uint)count,
                (nuint)(count * sizeof(ulong)),
                sizes,
                sizeof(ulong),
                VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait
            );
            if (read == VkResult.Success)
            {
                for (int i = 0; i < count; i++)
                {
                    compactable.Add((Built[i], sizes[i]));
                }
            }
            else
            {
                Log.Warn(
                    $"{count} acceleration structures keep their build's size: their sizes were not read, {read}."
                );
            }

            Built.Clear();
        }

        /// <summary>
        /// Records the frame's time on the GPU into <paramref name="frameDuration"/>, from its first timestamp to its
        /// last, and into <paramref name="passDuration"/> each pass's from its first timestamp to its last and each named
        /// stamp's from its pass's first, under its name. A frame with no timestamps records nothing.
        /// </summary>
        public void RecordTimings(Histogram<double> frameDuration, Histogram<double> passDuration)
        {
            int count = Timestamps.Count;
            if (count == 0)
                return;

            ulong* ticks = stackalloc ulong[count];
            VkResult read = _device.Api.vkGetQueryPoolResults(
                Queries,
                0,
                (uint)count,
                (nuint)(count * sizeof(ulong)),
                ticks,
                sizeof(ulong),
                VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait
            );
            if (read != VkResult.Success)
                return;

            double secPerTick = _device.TimestampPeriod / 1_000_000_000.0;
            frameDuration.Record((ticks[count - 1] - ticks[0]) * secPerTick);
            int passStart = 0;
            for (int i = 0; i < count; i++)
            {
                (string name, int pass) = Timestamps[i];
                bool isPassStart = i == 0 || Timestamps[i - 1].Pass != pass;
                if (isPassStart)
                    passStart = i;

                // A pass's last stamp times the pass; a named stamp in between times itself from the pass's start.
                bool isPassEnd = i == count - 1 || Timestamps[i + 1].Pass != pass;
                if (isPassEnd || !isPassStart)
                {
                    passDuration.Record(
                        (ticks[i] - ticks[passStart]) * secPerTick,
                        new KeyValuePair<string, object?>(PassTag, name)
                    );
                }
            }
        }
    }
}
