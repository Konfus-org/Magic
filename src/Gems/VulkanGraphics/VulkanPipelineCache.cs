using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The driver's cache of built pipelines, kept in a file from one run to the next, so a pipeline that an earlier run
/// built is built again fast.
/// </summary>
/// <remarks>
/// <para>
/// The cache is the record <c>PipelineCache/&lt;vendor&gt;-&lt;device&gt;</c> of <see cref="Caches"/>, one per GPU. A
/// record that another driver or GPU wrote, or that is not whole, is not given to the driver: the run starts with an
/// empty cache and writes over the record.
/// </para>
/// <para>
/// Vulkan synchronizes the cache itself, so any thread builds pipelines through it. A save and the dispose take turns.
/// </para>
/// </remarks>
internal sealed class VulkanPipelineCache : IDisposable
{
    /// <summary>
    /// The bytes of <c>VkPipelineCacheHeaderVersionOne</c>: its size, its version, the vendor's ID, the device's ID and
    /// the cache's UUID.
    /// </summary>
    private const int HeaderSize = (4 * sizeof(uint)) + UuidSize;

    private const int UuidSize = 16;

    private readonly VulkanDevice _device;

    private readonly Caches _caches;

    /// <summary>
    /// The name of the cache's record.
    /// </summary>
    private readonly string _record;

    private readonly Histogram<double> _buildDuration;

    private readonly Lock _saving = new();

    /// <summary>
    /// The pipelines built in this run and the stopwatch ticks they took, for the log line of each save.
    /// </summary>
    private long _builtCount;

    private long _builtTicks;

    public unsafe VulkanPipelineCache(VulkanDevice device, Caches caches, Meter meter)
    {
        _device = device;
        _caches = caches;
        _buildDuration = meter.CreateHistogram<double>(
            "magic.gems.vulkan_graphics.pipeline.load.duration",
            "s",
            "One pipeline's build, tagged with its kind: compute, bound or graphics."
        );
        device.InstanceApi.vkGetPhysicalDeviceProperties(device.PhysicalDevice, out VkPhysicalDeviceProperties gpu);
        _record = string.Create(CultureInfo.InvariantCulture, $"PipelineCache/{gpu.vendorID:x}-{gpu.deviceID:x}");
        byte[] initial = ReadRecord(gpu);
        fixed (byte* data = initial)
        {
            VkPipelineCacheCreateInfo createInfo = new()
            {
                initialDataSize = (nuint)initial.Length,
                pInitialData = data,
            };
            device
                .Api.vkCreatePipelineCache(&createInfo, null, out VkPipelineCache cache)
                .ThrowIfFailed("a pipeline cache");
            Handle = cache;
        }
    }

    public VkPipelineCache Handle { get; private set; }

    public void Dispose()
    {
        lock (_saving)
        {
            _device.Api.vkDestroyPipelineCache(Handle);
            Handle = VkPipelineCache.Null;
        }
    }

    /// <summary>
    /// Records a pipeline's build of <paramref name="kind"/>, which started at <paramref name="startTimestamp"/>.
    /// </summary>
    public void RecordBuild(long startTimestamp, string kind)
    {
        long ticks = Stopwatch.GetTimestamp() - startTimestamp;
        Interlocked.Increment(ref _builtCount);
        Interlocked.Add(ref _builtTicks, ticks);
        _buildDuration.Record(
            (double)ticks / Stopwatch.Frequency,
            new KeyValuePair<string, object?>(Metrics.PhaseTag, kind)
        );
    }

    /// <summary>
    /// Writes what the driver holds now over the record, and logs how many pipelines this run built and in how long.
    /// </summary>
    /// <remarks>
    /// When it cannot, <see cref="Caches"/> logs a warning, and the next run builds the pipelines again. Any thread.
    /// </remarks>
    public async Task SaveAsync()
    {
        if (ReadData() is not { } data)
            return;

        await _caches.WriteRecordAsync(_record, data).ConfigureAwait(false);
        double builtMs = Interlocked.Read(ref _builtTicks) * 1000.0 / Stopwatch.Frequency;
        Log.Info(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Pipelines: {Interlocked.Read(ref _builtCount)} built in {builtMs:0} ms so far; {data.Length / 1024} KB kept as {_record}."
            )
        );
    }

    /// <summary>
    /// What the driver holds now; null after the dispose, or when the driver cannot give it.
    /// </summary>
    private unsafe byte[]? ReadData()
    {
        lock (_saving)
        {
            if (Handle.IsNull)
                return null;

            nuint size = 0;
            _device.Api.vkGetPipelineCacheData(Handle, &size, null).ThrowIfFailed("the pipeline cache's size");
            byte[] data = new byte[(int)size];
            fixed (byte* pointer = data)
            {
                // The cache can grow between the two calls: then the driver writes less and says Incomplete.
                VkResult result = _device.Api.vkGetPipelineCacheData(Handle, &size, pointer);
                if (result is not (VkResult.Success or VkResult.Incomplete))
                {
                    Log.Warn(
                        $"The pipeline cache could not be read, so the next run builds its pipelines again: {result}."
                    );

                    return null;
                }
            }

            return data[..(int)size];
        }
    }

    /// <summary>
    /// The bytes of the record to start the cache with, when the driver and GPU of <paramref name="gpu"/> wrote them;
    /// none otherwise.
    /// </summary>
    private unsafe byte[] ReadRecord(in VkPhysicalDeviceProperties gpu)
    {
        if (_caches.ReadRecord(_record) is not { } bytes)
            return [];

        // The header, as Vulkan lays it out in the GPU's own byte order.
        if (bytes.Length < HeaderSize)
        {
            Log.Info($"{_record} is not whole, so pipelines are built again.");

            return [];
        }

        ReadOnlySpan<uint> header = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, HeaderSize - UuidSize));
        ReadOnlySpan<byte> uuid = bytes.AsSpan(HeaderSize - UuidSize, UuidSize);
        fixed (byte* expected = gpu.pipelineCacheUUID)
        {
            bool isOurs =
                header[0] >= HeaderSize
                && header[1] == (uint)VkPipelineCacheHeaderVersion.One
                && header[2] == gpu.vendorID
                && header[3] == gpu.deviceID
                && uuid.SequenceEqual(new ReadOnlySpan<byte>(expected, UuidSize));
            if (!isOurs)
            {
                Log.Info($"{_record} was written by another driver or GPU, so pipelines are built again.");

                return [];
            }
        }

        Log.Info(
            string.Create(CultureInfo.InvariantCulture, $"{_record} was read: {bytes.Length / 1024} KB of pipelines.")
        );

        return bytes;
    }
}
