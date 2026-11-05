using System.Numerics;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Ecs.Components.Effects;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.GIRendering;

/// <summary>
/// Reads what a frame draws out of the entities into a <see cref="RenderWorld"/>.
/// </summary>
internal static class WorldCopy
{
    /// <summary>
    /// What a surface facing the sun lands at in the picture before the tonemap, with no meter yet.
    /// </summary>
    private const float ExposureAtSun = 4;

    public static void Read(IEcs ecs, RenderWorld world)
    {
        world.Clear();
        ReadRenderers(ecs, world);
        ReadCamera(ecs, world);
        ReadSun(ecs, world);
        ReadLights(ecs, world);
        foreach (EcsChunk<Weather> chunk in ecs.Query<Weather>())
        {
            if (chunk.Entities.Length > 0 && world.Fog is null && chunk.First[0].Effects.TryGet(out Fog fog))
                world.Fog = fog;
        }
    }

    private static void ReadRenderers(IEcs ecs, RenderWorld world)
    {
        foreach (EcsChunk<Transform, Renderer> chunk in ecs.Query<Transform, Renderer>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                Renderer renderer = chunk.Second[i];
                if (renderer.Model != Handle<Model>.None)
                {
                    world.Instances.Add(
                        new RenderInstance(chunk.Entities[i], renderer.Model, renderer.Materials, chunk.First[i])
                    );
                }
            }
        }
    }

    private static void ReadCamera(IEcs ecs, RenderWorld world)
    {
        foreach (EcsChunk<Transform, Camera> chunk in ecs.Query<Transform, Camera>())
        {
            for (int i = 0; i < chunk.Entities.Length && world.Camera is null; i++)
            {
                Camera camera = chunk.Second[i];
                if (camera.Target.IsWindow)
                    world.Camera = new RenderCamera(chunk.First[i], camera.FieldOfView, camera.Near);
            }
        }

        foreach (EcsChunk<Camera, PostProcessing> chunk in ecs.Query<Camera, PostProcessing>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Target.IsWindow)
                    world.Post = chunk.Second[i];
            }
        }
    }

    /// <summary>
    /// The directional light the frame is lit by: the sun while it is up, else the moon while it is up, else the first
    /// there is.
    /// </summary>
    private static void ReadSun(IEcs ecs, RenderWorld world)
    {
        int sunRank = -1;
        foreach (EcsChunk<Transform, DirectionalLight> chunk in ecs.Query<Transform, DirectionalLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                DirectionalLight light = chunk.Second[i];
                Vector3 towards = -Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, chunk.First[i].Rotation));
                bool isUp = towards.Y > 0;
                int rank = 0;
                if (isUp && light.Mode == DirectionalLightMode.Sun)
                    rank = 2;
                else if (isUp && light.Mode == DirectionalLightMode.Moon)
                    rank = 1;

                if (rank <= sunRank)
                    continue;

                Vector3 illuminance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity;
                float exposure = light.Intensity > 0 ? ExposureAtSun / light.Intensity : 1;
                world.Sun = new RenderSun(towards, illuminance, exposure);
                sunRank = rank;
            }
        }
    }

    private static void ReadLights(IEcs ecs, RenderWorld world)
    {
        foreach (EcsChunk<Transform, PointLight> chunk in ecs.Query<Transform, PointLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                PointLight light = chunk.Second[i];
                world.Lights.Add(
                    new GpuLight
                    {
                        Position = chunk.First[i].Position,
                        Range = light.Reach,
                        Direction = Vector3.UnitZ,
                        Radius = light.Radius,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Point,
                    }
                );
            }
        }

        foreach (EcsChunk<Transform, SpotLight> chunk in ecs.Query<Transform, SpotLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                SpotLight light = chunk.Second[i];
                world.Lights.Add(
                    new GpuLight
                    {
                        Position = chunk.First[i].Position,
                        Range = light.Reach,
                        Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, chunk.First[i].Rotation)),
                        Radius = light.Radius,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Spot,
                        CosOuter = MathF.Cos(light.OuterAngle * MathF.PI / 180),
                        CosInner = MathF.Cos(light.FadeAngle * MathF.PI / 180),
                    }
                );
            }
        }

        foreach (EcsChunk<Transform, RectLight> chunk in ecs.Query<Transform, RectLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                RectLight light = chunk.Second[i];
                Transform at = chunk.First[i];
                world.Lights.Add(
                    new GpuLight
                    {
                        Position = at.Position,
                        Range = light.Reach,
                        Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, at.Rotation)),
                        Radius = 0.01f,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Rect,
                        Width = light.Width * Math.Abs(at.Scale.X),
                        Height = light.Height * Math.Abs(at.Scale.Y),
                    }
                );
            }
        }
    }
}
