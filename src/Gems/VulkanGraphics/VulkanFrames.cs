using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Utils;
using Vortice.Vulkan;

namespace VulkanGraphics;

/// <summary>
/// What a frame in flight owns: its command buffer, the fence that says the GPU is done with it, its staging ring,
/// its timestamps, what it threw away and what it promised to read back. Two of them take turns, so the CPU records
/// frame n + 1 while the GPU draws frame n and never waits for more than one.
/// </summary>
internal sealed unsafe class VulkanFrames : IDisposable
{
    public const int InFlight = 2;

    private const int TimestampCapacity = 512;
    private const long StagingBytes = 64L * 1024 * 1024;

    private readonly VulkanDevice _device;
    private readonly VulkanResources _resources;
    private readonly Frame[] _frames = new Frame[InFlight];
    private readonly List<Readback> _readbacks = [];
    private int _current;
    private ulong _serial;
    private ulong _completedSerial;

    public VulkanFrames(VulkanDevice device, VulkanResources resources)
    {
        _device = device;
        _resources = resources;
        for (int i = 0; i < InFlight; i++)
        {
            _frames[i] = new Frame(device, resources, i);
        }
    }

    public Frame Current => _frames[_current];

    /// <summary>
    /// The GPU's timing of the frame that last finished.
    /// </summary>
    public GpuTimings Timings { get; private set; } = GpuTimings.None;

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
    /// readbacks and reads its timestamps, then readies it to record. Main thread.
    /// </summary>
    public void Begin()
    {
        _current = (_current + 1) % InFlight;
        Frame frame = _frames[_current];
        if (frame.Serial > 0 && !frame.IsCompleted)
        {
            VkResult waited = _device.Api.vkWaitForFences(frame.Fence, true, ulong.MaxValue);
            if (waited != VkResult.Success)
                Log.Error($"Waiting for frame {frame.Serial} failed: {waited}.");

            Complete(frame);
        }

        _device.Api.vkResetFences(frame.Fence);
        _device.Api.vkResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);
        frame.Serial = ++_serial;
        frame.IsCompleted = false;
        frame.StagingOffset = 0;
        frame.Timestamps.Clear();
        VkCommandBufferBeginInfo beginInfo = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _device.Api.vkBeginCommandBuffer(frame.CommandBuffer, &beginInfo);
        _device.Api.vkCmdResetQueryPool(frame.CommandBuffer, frame.Queries, 0, TimestampCapacity);
    }

    /// <summary>
    /// Finishes every frame the GPU is done with, without waiting: what readbacks and trash wait for while no frame
    /// begins, as during the awaited start of a run. Main thread.
    /// </summary>
    public void Poll()
    {
        foreach (Frame frame in _frames)
        {
            if (frame.Serial == 0 || frame.IsCompleted)
                continue;

            if (_device.Api.vkGetFenceStatus(frame.Fence) == VkResult.Success)
                Complete(frame);
        }
    }

    /// <summary>
    /// Room in the current frame's staging ring, grown when a frame needs more than it had.
    /// </summary>
    public GpuUpload Upload(int bytes, int alignment)
    {
        Frame frame = Current;
        long offset = (frame.StagingOffset + alignment - 1) / alignment * alignment;
        if (offset + bytes > frame.Staging.Description.Bytes)
        {
            long wanted = Math.Max(frame.Staging.Description.Bytes * 2, offset + bytes);
            Result<VulkanBuffer> grown = _resources.MakeBuffer(
                new BufferDescription
                {
                    Bytes = wanted,
                    Usage = BufferUsages.Upload,
                    Name = $"Staging {frame.Index}",
                }
            );
            if (!grown.Ok)
                throw new InvalidOperationException(
                    $"The staging ring could not grow to {wanted} bytes: {grown.Error}"
                );

            // The old ring leaves the heap now, so shutdown frees it once, with the frame's trash.
            frame.Trash.Add(Trash.Of(frame.StagingId, _resources.RemoveBuffer(frame.StagingId) ?? frame.Staging));
            frame.Staging = grown.Value;
            frame.StagingId = Register(grown.Value);
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
    /// Main thread.
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
    /// Finishes what the GPU is done with: the frame's trash, the readbacks up to its serial, its timestamps.
    /// </summary>
    private void Complete(Frame frame)
    {
        frame.IsCompleted = true;
        _completedSerial = Math.Max(_completedSerial, frame.Serial);
        foreach (Trash trash in frame.Trash)
        {
            trash.Free(_resources);
        }

        frame.Trash.Clear();
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

        Timings = frame.ReadTimings(_device);
    }

    private uint Register(VulkanBuffer buffer)
    {
        Result<GpuBuffer> registered = _resources.Adopt(buffer);
        if (!registered.Ok)
            throw new InvalidOperationException($"The staging ring has no place in the heap: {registered.Error}");

        return registered.Value.Id;
    }

    /// <summary>
    /// A resource freed by a frame, to die once the frame has run: whichever of its fields its kind uses.
    /// </summary>
    public readonly record struct Trash(
        uint Id,
        VulkanBuffer? Buffer,
        VulkanTexture? Texture,
        VkSampler Sampler,
        VulkanPipeline? Pipeline,
        VulkanAcceleration? Acceleration = null
    )
    {
        public static Trash Of(uint id, VulkanBuffer buffer) => new(id, buffer, null, VkSampler.Null, null);

        public static Trash Of(uint id, VulkanTexture texture) => new(id, null, texture, VkSampler.Null, null);

        public static Trash Of(uint id, VkSampler sampler) => new(id, null, null, sampler, null);

        public static Trash Of(uint id, VulkanPipeline pipeline) => new(id, null, null, VkSampler.Null, pipeline);

        public static Trash Of(uint id, VulkanAcceleration acceleration) =>
            new(id, null, null, VkSampler.Null, null, acceleration);

        public void Free(VulkanResources resources)
        {
            if (Buffer is not null)
                resources.Free(Id, Buffer);
            else if (Texture is not null)
                resources.Free(Id, Texture);
            else if (Pipeline is not null)
                resources.Free(Id, Pipeline);
            else if (Acceleration is not null)
                resources.Free(Id, Acceleration);
            else if (Sampler.IsNotNull)
                resources.Free(Id, Sampler);
        }
    }

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

        public Frame(VulkanDevice device, VulkanResources resources, int index)
        {
            _device = device;
            Index = index;
            VkResult made = device.Api.vkCreateCommandPool(
                VkCommandPoolCreateFlags.Transient,
                device.QueueFamily,
                out VkCommandPool pool
            );
            Check(made, "a command pool");
            Pool = pool;
            VkCommandBufferAllocateInfo allocateInfo = new()
            {
                commandPool = pool,
                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer commandBuffer;
            Check(device.Api.vkAllocateCommandBuffers(&allocateInfo, &commandBuffer), "a command buffer");
            CommandBuffer = commandBuffer;
            Check(device.Api.vkCreateFence(VkFenceCreateFlags.Signaled, out VkFence fence), "a fence");
            Fence = fence;
            Check(device.Api.vkCreateSemaphore(out VkSemaphore acquired), "a semaphore");
            Acquired = acquired;
            VkQueryPoolCreateInfo queryInfo = new()
            {
                queryType = VkQueryType.Timestamp,
                queryCount = TimestampCapacity,
            };
            Check(device.Api.vkCreateQueryPool(&queryInfo, out VkQueryPool queries), "a query pool");
            Queries = queries;
            Result<VulkanBuffer> staging = resources.MakeBuffer(
                new BufferDescription
                {
                    Bytes = StagingBytes,
                    Usage = BufferUsages.Upload,
                    Name = $"Staging {index}",
                }
            );
            if (!staging.Ok)
                throw new InvalidOperationException($"The staging ring was not made: {staging.Error}");

            Staging = staging.Value;
            Result<GpuBuffer> adopted = resources.Adopt(staging.Value);
            if (!adopted.Ok)
                throw new InvalidOperationException($"The staging ring has no place in the heap: {adopted.Error}");

            StagingId = adopted.Value.Id;
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

        public VulkanBuffer Staging { get; set; }

        public uint StagingId { get; set; }

        public long StagingOffset { get; set; }

        /// <summary>
        /// Which submission the frame holds; 0 before its first.
        /// </summary>
        public ulong Serial { get; set; }

        /// <summary>
        /// Whether what the frame last held was finished with, so it is not finished twice.
        /// </summary>
        public bool IsCompleted { get; set; }

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
            _device.Api.vkDestroySemaphore(Acquired);
            _device.Api.vkDestroyFence(Fence);
            _device.Api.vkDestroyCommandPool(Pool);
        }

        /// <summary>
        /// Writes the next timestamp, named, and says which query it is; -1 when the frame has no room for more.
        /// </summary>
        public int WriteTimestamp(VulkanDevice device, string name, int pass)
        {
            if (Timestamps.Count >= TimestampCapacity)
                return -1;

            int query = Timestamps.Count;
            Timestamps.Add((name, pass));
            device.Api.vkCmdWriteTimestamp2(CommandBuffer, VkPipelineStageFlags2.AllCommands, Queries, (uint)query);

            return query;
        }

        /// <summary>
        /// Each pass's time from its first timestamp to its last, and each named stamp's from its pass's first.
        /// </summary>
        public GpuTimings ReadTimings(VulkanDevice device)
        {
            int count = Timestamps.Count;
            if (count == 0)
                return new GpuTimings((uint)Serial, []);

            ulong* ticks = stackalloc ulong[count];
            VkResult read = device.Api.vkGetQueryPoolResults(
                Queries,
                0,
                (uint)count,
                (nuint)(count * sizeof(ulong)),
                ticks,
                sizeof(ulong),
                VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait
            );
            if (read != VkResult.Success)
                return new GpuTimings((uint)Serial, []);

            double msPerTick = device.TimestampPeriod / 1_000_000.0;
            ImmutableArray<GpuTiming>.Builder timings = ImmutableArray.CreateBuilder<GpuTiming>();
            int passStart = 0;
            for (int i = 0; i < count; i++)
            {
                (string name, int pass) = Timestamps[i];
                bool isPassStart = i == 0 || Timestamps[i - 1].Pass != pass;
                if (isPassStart)
                    passStart = i;

                bool isPassEnd = i == count - 1 || Timestamps[i + 1].Pass != pass;
                if (isPassEnd)
                    timings.Add(new GpuTiming(name, (ticks[i] - ticks[passStart]) * msPerTick));
                else if (!isPassStart)
                    timings.Add(new GpuTiming(name, (ticks[i] - ticks[passStart]) * msPerTick));
            }

            return new GpuTimings((uint)Serial, timings.ToImmutable());
        }

        private static void Check(VkResult result, string what)
        {
            if (result != VkResult.Success)
                throw new InvalidOperationException($"Vulkan could not make {what} for a frame: {result}.");
        }
    }
}
