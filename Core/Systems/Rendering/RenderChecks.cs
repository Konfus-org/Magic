using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Systems.Rendering;

/// <summary>
/// Everything <see cref="IRendering.Debug"/> turns on, in one place, through the same <see cref="IRendering"/> the frame uses: start-up
/// checks of the conventions every shader is written against (a clockwise triangle is the one that survives back-face
/// culling; a System.Numerics matrix uploaded untransposed and applied with <c>mul(M, v)</c> agrees with
/// <c>Vector4.Transform</c>, on this backend), and a periodic check of the GPU cull against a CPU cull of the same rows. A
/// failure is logged as an error, so a run with <c>--fail-on-error</c> fails.
/// </summary>
internal static class RenderChecks
{
    /// <summary>How often <see cref="Verify"/> runs: its readbacks stall the frame.</summary>
    public const int VerifyEveryFrames = 60;

    public static void RunProbes(RenderContext ctx)
    {
        CheckWinding(ctx);
        CheckMatrices(ctx);
    }

    /// <summary>
    /// Reads the view's draw args back (synchronously) and compares the GPU's early and late visible counts with the CPU's
    /// bounds for frustum and size alone (occlusion has no CPU twin): the instances that are certainly visible and the ones
    /// that possibly are.
    /// </summary>
    public static void Verify(RenderContext ctx, ViewBuffers view, in FrameConstants frame)
    {
        uint argsBytes = (uint)ctx.Buckets.ChunkCount * Buckets.ChunkBytes;
        if (argsBytes == 0)
            return;

        Result<byte[]> earlyArgs = ctx.Gpu.Read(view.DrawArgsEarly.Handle, argsBytes);
        if (earlyArgs.Failed)
        {
            Debugging.Log.Error($"Culling check: reading the draw args failed: {earlyArgs.Message}");
            return;
        }

        uint early = VisibleCount(earlyArgs.Payload);
        // Without occlusion the late pass never runs, so there are no late args.
        uint late = view.DrawArgsLate is { } lateArgs && ctx.Gpu.Read(lateArgs.Handle, argsBytes) is { Ok: true } read ? VisibleCount(read.Payload) : 0;

        // The CPU side of the frustum and size tests, from the Core maths rather than the shader. A 2% band around each
        // threshold, because edge instances flip between the two float paths.
        Frustum frustum = Frustum.FromViewProjection(frame.ViewProj);
        Vector3 camera = frame.CameraPos;
        uint cpuMin = 0, cpuMax = 0;
        foreach (ref readonly GpuInstance row in ctx.Instances.Rows)
        {
            if (!row.Flags.HasFlag(InstanceFlags.Alive))
                continue;

            Vector3 center = new Vector3(row.Sphere.X, row.Sphere.Y, row.Sphere.Z) - camera;
            float radius = row.Sphere.W;
            bool surely = true, maybe = true;
            if (frame.IsOrthographic == 0)
            {
                surely &= frustum.Intersects(new BoundingSphere(center, radius * 0.99f));
                maybe &= frustum.Intersects(new BoundingSphere(center, radius * 1.01f));
                if (!row.Flags.HasFlag(InstanceFlags.NoSizeCull))
                {
                    float z = Vector3.TransformNormal(center, frame.View).Z;
                    float pixels = radius * frame.ProjScale.Y * frame.ViewSize.Y * 0.5f / MathF.Max(z, frame.Near);
                    surely &= pixels >= frame.MinPixels * 1.02f;
                    maybe &= pixels >= frame.MinPixels * 0.98f;
                }
            }

            if (surely)
                cpuMin++;
            if (maybe)
                cpuMax++;
        }

        uint gpuCount = early + late;
        uint alive = ctx.Instances.Alive;
        // Occlusion only ever removes: with it on, the GPU may draw fewer than the CPU's frustum-only count.
        bool ok = view.DrawArgsLate is not null ? gpuCount <= cpuMax : gpuCount >= cpuMin && gpuCount <= cpuMax;
        if (!ok)
            Debugging.Log.Error($"Culling: GPU drew {early} + {late} instances, the CPU reference says {cpuMin}..{cpuMax} (of {alive} alive).");
        else
            Debugging.Log.Debug($"Culling verified: {early} early + {late} late of {alive} instances (frustum and size alone: {cpuMin}..{cpuMax}).");
    }

    private static uint VisibleCount(byte[] drawArgs)
    {
        uint count = 0;
        foreach (DrawArgs args in MemoryMarshal.Cast<byte, DrawArgs>(drawArgs))
            count += args.InstanceCount;

        return count;
    }

    /// <summary>Two triangles straight from a vertex buffer in NDC, one wound each way; only the clockwise one may survive.</summary>
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
                Debugging.Log.Info($"Probe: CW triangle visible, CCW culled ({gpu.Device}).");
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
            Culling.Dispatch(commands, pipeline, [output], [input], 1);
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
                Debugging.Log.Info($"Probe: matrices agree ({gpu.Device}): cbuffer mul(M, v) and 3x4 rows match Vector4.Transform.");
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
