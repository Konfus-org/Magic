using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Everything <see cref="IRendering.Debug"/> turns on, in one place, through the same <see cref="IRendering"/> the frame uses: start-up
/// checks of the conventions every shader is written against (a clockwise triangle is the one that survives back-face
/// culling; a System.Numerics matrix uploaded untransposed and applied with <c>mul(M, v)</c> agrees with
/// <c>Vector4.Transform</c>, on this backend), and, with <see cref="Magic.Contexts.Settings.RenderSettings.CullingCheck"/>, a periodic check of the GPU cull against a CPU cull of the same rows. A
/// failure is logged as an error, so a run with <c>--fail-on-error</c> fails.
/// </summary>
internal static class RenderChecks
{
    /// <summary>
    /// How often <see cref="Verify"/> runs: its readbacks stall the frame.
    /// </summary>
    public const int VerifyEveryFrames = 60;

    /// <summary>
    /// The share of a LOD threshold over which an instance blends into the next version of its mesh: the cull passes'
    /// <c>LOD_BLEND</c> (Resources/Passes/Core/CullEarly.pass), which the CPU's count has to agree with.
    /// </summary>
    private const float LodBlend = 0.25f;

    public static void RunProbes(RenderContext ctx)
    {
        CheckWinding(ctx);
        CheckMatrices(ctx);
    }

    /// <summary>
    /// Reads the view's draw args back (synchronously, by the names the scene passes create them under) and compares the
    /// GPU's early and late visible counts with the CPU's bounds for frustum and size alone (occlusion has no CPU twin):
    /// the instances that are certainly visible and the ones that possibly are. Nothing without a scene stage that made them.
    /// </summary>
    public static void Verify(RenderContext ctx, ResourceSet view, in FrameConstants frame)
    {
        uint argsBytes = (uint)ctx.Buckets.ChunkCount * Buckets.ChunkBytes;
        if (argsBytes == 0 || view.GetValueOrDefault("DrawArgsEarly") is not { Buffer: { } earlyBuffer })
            return;

        Result<byte[]> earlyArgs = ctx.Gpu.Read(earlyBuffer.Handle, argsBytes);
        if (earlyArgs.Failed)
        {
            Debugging.Log.Error($"Culling check: reading the draw args failed: {earlyArgs.Message}");
            return;
        }

        uint early = VisibleCount(earlyArgs.Payload);
        // Without occlusion the late pass never runs, so there are no late args.
        GrowableBuffer? lateBuffer = view.GetValueOrDefault("DrawArgsLate")?.Buffer;
        bool occlusion = lateBuffer is not null;
        uint late = lateBuffer is not null && ctx.Gpu.Read(lateBuffer.Handle, argsBytes) is { Ok: true } read ? VisibleCount(read.Payload) : 0;

        // The CPU side of the frustum and size tests, from the Core maths rather than the shader. A 2% band around each
        // threshold, because edge instances flip between the two float paths.
        Frustum frustum = Frustum.FromViewProjection(frame.ViewProj);
        Vector3 camera = frame.CameraPos;
        uint cpuMin = 0, cpuMax = 0;
        foreach (ref readonly GpuInstance row in ctx.Instances.Rows)
        {
            if ((row.Flags & InstanceFlags.Alive) == 0 || (row.Flags & InstanceFlags.Hidden) != 0)
                continue;

            Vector3 center = new Vector3(row.Sphere.X, row.Sphere.Y, row.Sphere.Z) - camera;
            float radius = row.Sphere.W;
            bool surely = true, maybe = true;
            bool surelyBlends = false, maybeBlends = false;
            if (frame.IsOrthographic == 0)
            {
                surely &= frustum.Intersects(new BoundingSphere(center, radius * 0.99f));
                maybe &= frustum.Intersects(new BoundingSphere(center, radius * 1.01f));
                float z = MathF.Max(Vector3.TransformNormal(center, frame.View).Z, frame.Near);
                if ((row.Flags & InstanceFlags.NoSizeCull) == 0)
                {
                    float pixels = row.CullRadius * frame.ProjScale.Y * frame.ViewSize.Y * 0.5f / z;
                    surely &= pixels >= frame.MinPixels * 1.02f;
                    maybe &= pixels >= frame.MinPixels * 0.98f;
                }

                // Blending between two versions of its mesh, it is drawn in both: the last threshold it is under decides.
                float height = radius * frame.ProjScale.Y / z * frame.LodBias;
                GpuLodRow lods = ctx.Buckets.LodRows[(int)row.BucketGroup];
                for (int i = 0; i < lods.Count; i++)
                {
                    float threshold = lods.Thresholds[i];
                    float blendEnd = threshold * (1f - LodBlend);
                    maybeBlends |= height > blendEnd * 0.98f && height < threshold * 1.02f;
                    if (height < threshold * 0.98f)
                        surelyBlends = height > blendEnd * 1.02f;
                    else if (height < threshold * 1.02f)
                        surelyBlends = false;
                }
            }

            if (surely)
                cpuMin += surelyBlends ? 2u : 1u;
            if (maybe)
                cpuMax += maybeBlends ? 2u : 1u;
        }

        uint gpuCount = early + late;
        uint alive = ctx.Instances.Alive;
        // Occlusion only ever removes: with it on, the GPU may draw fewer than the CPU's frustum-only count.
        bool ok = occlusion ? gpuCount <= cpuMax : gpuCount >= cpuMin && gpuCount <= cpuMax;
        if (!ok)
            Debugging.Log.Error($"Culling: GPU drew {early} + {late} instances, the CPU reference says {cpuMin}..{cpuMax} (of {alive} alive).");
        else
            Debugging.Log.Verbose($"Culling verified: {early} early + {late} late of {alive} instances (frustum and size alone: {cpuMin}..{cpuMax}).");
    }

    /// <summary>
    /// Reads the shadow views back (synchronously) and holds every cascade the GPU fitted this frame against the reference
    /// fit of the same slice from the same camera and parameters (<see cref="Cascades.Fit"/>): the centre and the texel
    /// size agree to within a texel. Nothing without a shadow plan pass, or while the sun casts no shadow.
    /// </summary>
    public static void VerifyShadowPlan(RenderContext ctx, in FrameConstants frame)
    {
        PassState? plan = null;
        foreach (PassState pass in ctx.Pipeline.Stages[(int)Magic.Contexts.Assets.PipelineStage.Shadows])
        {
            if (pass.Ready && pass.Values.ContainsKey("cascades") && pass.Values.ContainsKey("distance"))
                plan = pass;
        }

        ResourceScope scope = new(ctx, null, null);
        if (plan is null || !scope.TryBuffer("ShadowViews", out GpuBuffer views, out _) || !scope.TryBuffer("ShadowHeader", out GpuBuffer header, out _))
        {
            Debugging.Log.Verbose($"Shadow plan check: nothing to check ({(plan is null ? "no shadow plan pass runs" : "the shadow views are not made")}).");
            return;
        }

        Result<byte[]> headerBytes = ctx.Gpu.Read(header, GpuShadowHeader.Size);
        Result<byte[]> viewBytes = ctx.Gpu.Read(views, (uint)(Cascades.MaxCascades * GpuShadowView.Size));
        if (headerBytes.Failed || viewBytes.Failed)
        {
            Debugging.Log.Error($"Shadow plan check: reading the shadow views failed: {(headerBytes.Failed ? headerBytes.Message : viewBytes.Message)}");
            return;
        }

        GpuShadowHeader planned = MemoryMarshal.Read<GpuShadowHeader>(headerBytes.Payload);
        ReadOnlySpan<GpuShadowView> rows = MemoryMarshal.Cast<byte, GpuShadowView>(viewBytes.Payload);
        if (planned.CascadeCount == 0)
        {
            Debugging.Log.Verbose("Shadow plan check: no cascades this frame (the sun casts no shadow).");
            return;
        }

        float Value(string name, float fallback) => plan.Values.TryGetValue(name, out Magic.Contexts.Assets.Param value) ? value.X : fallback;
        int count = (int)planned.CascadeCount;
        int resolution = (int)Value("cascadeResolution", 2048f);
        float distance = MathF.Max(Value("distance", 300f), frame.Near + 1f);
        float lambda = Value("splitLambda", 0.7f);
        float blend = Value("blendFraction", 0.15f);
        float casterRange = Value("casterRange", 500f);

        // The camera from its constants: the view rows are its axes.
        Matrix4x4 cameraWorld = Matrix4x4.Transpose(frame.View);
        cameraWorld.Translation = frame.CameraPos;
        float tanHalf = 1f / frame.ProjScale.Y;
        float fov = 2f * MathF.Atan(tanHalf);
        float aspect = frame.ProjScale.Y / frame.ProjScale.X;
        Matrix4x4 lightRotation = Cascades.LightRotation(frame.SunDirection);
        Span<float> fars = stackalloc float[count];
        Cascades.Split(frame.Near, distance, lambda, fars);

        int checkedRows = 0;
        for (int i = 0; i < count; i++)
        {
            GpuShadowView row = rows[i];
            if (row.Flags.Y == 0)
                continue; // kept from an earlier frame: fitted from an earlier camera

            float nearSplit = i == 0 ? frame.Near : fars[i - 1] * (1f - blend);
            Cascade expected = Cascades.Fit(cameraWorld, fov, aspect, nearSplit, fars[i], lightRotation, resolution, casterRange);
            float texel = expected.TexelWorld;
            Vector3 eye = new(row.Eye.X, row.Eye.Y, row.Eye.Z);
            bool agrees = Vector3.Distance(eye, expected.Center) <= texel && MathF.Abs(row.Eye.W - texel) <= texel * 0.01f && MathF.Abs(row.Range.X - expected.FarSplit) <= 0.01f * expected.FarSplit;
            if (!agrees)
                Debugging.Log.Error($"Shadow plan: cascade {i} fitted at {eye} ({row.Eye.W:F4} m/texel, ends {row.Range.X:F1} m); the reference says {expected.Center} ({texel:F4} m/texel, ends {expected.FarSplit:F1} m).");
            checkedRows++;
        }

        Debugging.Log.Verbose($"Shadow plan verified: {checkedRows} cascade(s) refitted this frame agree with the reference.");
    }

    private static uint VisibleCount(byte[] drawArgs)
    {
        uint count = 0;
        foreach (DrawArgs args in MemoryMarshal.Cast<byte, DrawArgs>(drawArgs))
            count += args.InstanceCount;

        return count;
    }

    /// <summary>
    /// Two triangles straight from a vertex buffer in NDC, one wound each way; only the clockwise one may survive.
    /// </summary>
    private static void CheckWinding(RenderContext ctx)
    {
        TriangleVertex[] vertices =
        [
            // Clockwise on screen (+Y up): bottom left, top, bottom right. Visible.
            new(new Vector4(-0.9f, -0.5f, 0.5f, 1f), Vector4.One),
            new(new Vector4(-0.5f, 0.5f, 0.5f, 1f), Vector4.One),
            new(new Vector4(-0.1f, -0.5f, 0.5f, 1f), Vector4.One),
            // Counter-clockwise: bottom left, bottom right, top. Culled.
            new(new Vector4(0.1f, -0.5f, 0.5f, 1f), new Vector4(1f, 0f, 0f, 1f)),
            new(new Vector4(0.9f, -0.5f, 0.5f, 1f), new Vector4(1f, 0f, 0f, 1f)),
            new(new Vector4(0.5f, 0.5f, 0.5f, 1f), new Vector4(1f, 0f, 0f, 1f)),
        ];

        IRendering gpu = ctx.Gpu;
        CompiledShader vertexShader, fragmentShader;
        try
        {
            vertexShader = Shaders.CompileBuiltIn(ctx, "Test/Triangle.vert.hlsl", GpuStage.Vertex);
            fragmentShader = Shaders.CompileBuiltIn(ctx, "Test/Triangle.frag.hlsl", GpuStage.Fragment);
        }
        catch (InvalidOperationException ex)
        {
            Debugging.Log.Error($"Probe: the triangle shaders did not compile: {ex.Message}");
            return;
        }

        const uint size = 64;
        GpuPipeline pipeline = gpu.CreatePipeline(new PipelineDesc(vertexShader, fragmentShader, FrameTargets.LdrFormat)
        {
            Buffers = [new VertexBufferLayout(0, 32)],
            Attributes = [new VertexAttribute(0, 0, GpuVertexFormat.Float4, 0), new VertexAttribute(1, 0, GpuVertexFormat.Float4, 16)],
        });
        GpuBuffer buffer = gpu.CreateBuffer(GpuBufferUsage.Vertex, (uint)(vertices.Length * 32));
        GpuTexture target = gpu.CreateTexture(new TextureDesc(FrameTargets.LdrFormat, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler, size, size));
        try
        {
            gpu.Upload<TriangleVertex>(buffer, 0, vertices);
            RenderCommands commands = new();
            commands.BeginRenderPass(target, GpuLoad.Clear);
            commands.SetViewport(new System.Drawing.Rectangle(0, 0, (int)size, (int)size));
            commands.BindPipeline(pipeline);
            commands.BindVertexBuffers(0, [buffer]);
            commands.Draw((uint)vertices.Length);
            commands.EndRenderPass();
            gpu.Submit(commands);

            Result<CapturedFrame> frame = gpu.Read(target);
            if (frame.Failed)
            {
                Debugging.Log.Error($"Probe: winding readback failed: {frame.Message}");
                return;
            }

            // The clockwise triangle spans x in -0.9..-0.1 (columns 3..28), the other 0.1..0.9 (36..61); y = 0 is row 32.
            byte cw = frame.Payload.Pixels[((32 * (int)size) + 16) * 4];
            byte ccw = frame.Payload.Pixels[((32 * (int)size) + 48) * 4];

            if (cw > 200 && ccw < 50)
                Debugging.Log.Verbose($"Probe: CW triangle visible, CCW culled ({gpu.Device}).");
            else
                Debugging.Log.Error($"Probe: winding is wrong on {gpu.Device}: clockwise pixel {cw}, counter-clockwise pixel {ccw} (expected >200 and <50).");
        }
        finally
        {
            gpu.Release(target);
            gpu.Release(buffer);
            gpu.Release(pipeline);
        }
    }

    private static void CheckMatrices(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        GpuPipeline pipeline;
        try
        {
            pipeline = gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, "Test/MatrixProbe.comp.hlsl", GpuStage.Compute));
        }
        catch (InvalidOperationException ex)
        {
            Debugging.Log.Error($"Probe: {ex.Message}");
            return;
        }

        GpuBuffer input = gpu.CreateBuffer(GpuBufferUsage.ComputeRead, (uint)Marshal.SizeOf<MatrixProbeInput>());
        GpuBuffer output = gpu.CreateBuffer(GpuBufferUsage.ComputeWrite, 32);
        try
        {
            Matrix4x4 projection = Matrix4x4.CreateFromYawPitchRoll(0.3f, 0.7f, -0.2f) * Matrix4x4.CreateTranslation(1f, 2f, 3f) * Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 100f);
            Matrix4x4 world = Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateFromYawPitchRoll(1f, 0.2f, 0.4f) * Matrix4x4.CreateTranslation(-5f, 6f, 7f);
            Vector4 vector = new(0.5f, -1.5f, 2.5f, 1f);
            Matrix4x4 transposed = Matrix4x4.Transpose(world);
            MatrixProbeInput probe = new()
            {
                V = vector,
                R0 = new Vector4(transposed.M11, transposed.M12, transposed.M13, transposed.M14),
                R1 = new Vector4(transposed.M21, transposed.M22, transposed.M23, transposed.M24),
                R2 = new Vector4(transposed.M31, transposed.M32, transposed.M33, transposed.M34),
            };

            gpu.Upload<MatrixProbeInput>(input, 0, [probe]);
            RenderCommands commands = new();
            commands.Push(GpuStage.Compute, projection);
            commands.BeginComputePass([new GpuBinding(output)]);
            commands.BindPipeline(pipeline);
            commands.BindStorageBuffers(GpuStage.Compute, 0, [input]);
            commands.Dispatch(1);
            commands.EndComputePass();
            gpu.Submit(commands);

            Result<byte[]> read = gpu.Read(output, 32);
            if (read.Failed)
            {
                Debugging.Log.Error($"Probe: matrix readback failed: {read.Message}");
                return;
            }

            ReadOnlySpan<Vector4> results = MemoryMarshal.Cast<byte, Vector4>(read.Payload);
            Vector4 cbuffer = results[0];
            Vector4 rows = results[1];

            Vector4 expectedCbuffer = Vector4.Transform(vector, projection);
            Vector4 expectedRows = Vector4.Transform(vector, world);
            bool ok = NearlyEqual(cbuffer, expectedCbuffer) && NearlyEqual(new Vector4(rows.X, rows.Y, rows.Z, expectedRows.W), expectedRows);

            if (ok)
                Debugging.Log.Verbose($"Probe: matrices agree ({gpu.Device}): cbuffer mul(M, v) and 3x4 rows match Vector4.Transform.");
            else
                Debugging.Log.Error($"Probe: matrices disagree on {gpu.Device}: cbuffer {cbuffer} vs {expectedCbuffer}; rows {rows} vs {expectedRows}.");
        }
        finally
        {
            gpu.Release(input);
            gpu.Release(output);
            gpu.Release(pipeline);
        }
    }

    private static bool NearlyEqual(Vector4 value, Vector4 expected)
    {
        return Vector4.Distance(value, expected) <= 1e-3f * MathF.Max(1f, expected.Length());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TriangleVertex(Vector4 position, Vector4 color)
    {
        public Vector4 Position = position;
        public Vector4 Color = color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatrixProbeInput
    {
        public Vector4 V, R0, R1, R2;
    }
}
