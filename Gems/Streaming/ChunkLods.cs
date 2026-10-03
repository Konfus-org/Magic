using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Services;
using Magic.Utils;
using System.IO.Hashing;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace StreamingGem;

/// <summary>
/// Makes the stand-in of a chunk that has none: what the streaming system spawns in its place beyond
/// <see cref="Distance"/> metres. Everything the chunk draws that never moves (an entity tagged static with a
/// <see cref="Renderer"/>) is merged into one model per eight materials, in world space, and the stand-in is the few
/// entities that draw those, plus a few point lights that stand for all the chunk's point and spot lights
/// (<see cref="LightsAcross"/>), so what is in view far away is still lit, by fewer lights, and a <see cref="Glow"/>
/// where each of those lights was (one for the lights that stand close together, <see cref="GlowSpacing"/>), so
/// each still shows as the bright dot it is from there; nothing else of the
/// chunk is in it, so far away its scripts do not run. The objects are sorted into sizes, four to a doubling of the radius, and every size is merged apart
/// and drawn with the radius of its biggest object as its <see cref="Renderer.CullRadius"/>: the renderer then stops drawing a size when its
/// objects would be too small on screen to see, at the resolution and field of view there are, exactly as it does
/// for the objects of a chunk that is spawned whole. An object under <see cref="BoxUnder"/> metres in radius is drawn
/// as its box (without normals, which the renderer draws flat: every face lit as the face it is), a larger one as the lowest LOD of its model;
/// and when a chunk has more than <see cref="MaxVertices"/> or <see cref="MaxIndices"/> to show, the smallest objects
/// are left out. The budget is small because every stand-in
/// in view lives in the renderer's mesh buffers at once.
/// </summary>
internal sealed class ChunkLods(Assets assets, IFileSystem files) : ILODGenerator<Chunk>
{
    /// <summary>
    /// Metres from a camera beyond which the stand-in is spawned instead of the chunk.
    /// </summary>
    private const float Distance = 512f;

    /// <summary>
    /// An object whose bounds have a smaller radius than this is its box in the stand-in: at <see cref="Distance"/> it is a few pixels.
    /// </summary>
    private const float BoxUnder = 4f;

    /// <summary>
    /// How finely objects are sorted by size. A size is culled as its biggest object would be, so the smallest of it
    /// stays up to a fifth longer than it would on its own; fewer sizes would leave a visible band of them.
    /// </summary>
    private const int SizesPerDoubling = 4;

    /// <summary>
    /// The most vertices one stand-in holds, over all its materials.
    /// </summary>
    private const int MaxVertices = 12288;

    /// <summary>
    /// The most indices one stand-in holds, over all its materials.
    /// </summary>
    private const int MaxIndices = 49152;

    /// <summary>
    /// The chunk's lights are gathered into this many a side of the ground they stand on, each lot merged into one
    /// point light at their middle with their light added up and a range that covers them all. More keeps the far
    /// lighting closer to the chunk's own; fewer puts fewer lights into the far screen tiles, where a tile covers the
    /// most ground and its lights run out first.
    /// </summary>
    private const int LightsAcross = 2;

    /// <summary>
    /// Metres, the radius of the glow that stands for a light of intensity 1; a glow grows with the square root of
    /// its light's intensity (four times the light, twice the dot), between <see cref="GlowRadiusMin"/> and
    /// <see cref="GlowRadiusMax"/>. Bigger makes far lights read as lamps sooner but lets a field of them run
    /// together. On screen a glow is never under a pixel, so this matters most near the distance the stand-in
    /// takes over at; farther away a small glow is a dimmer point.
    /// </summary>
    private const float GlowRadius = 0.08f;

    /// <summary>
    /// Metres. Lights that stand in the same cube of this size share one glow: from where a stand-in is seen they
    /// are one point of light anyway, and a string of lamps on one post is a dot, not a smear. Bigger thins the far
    /// lights out more, until separate lamps visibly become one.
    /// </summary>
    private const float GlowSpacing = 8f;

    private const float GlowRadiusMin = 0.05f;

    private const float GlowRadiusMax = 1f;

    /// <summary>
    /// How bright a light's glow is: its colour at full strength times this. Above 1 so it still reads as a light
    /// after the tonemap; much higher and every glow burns out to white.
    /// </summary>
    private const float GlowBrightness = 2f;

    public int Version => 12;

    public async Task<Result<Dictionary<float, string>>> GenerateAsync(Chunk asset, string folder, IProgress<float>? progress, CancellationToken cancel)
    {
        // What the chunk draws, the biggest first, so that what does not fit is the smallest.
        List<(Mesh Mesh, Matrix4x4 World, Handle<Material> Material, float Radius)> drawn = [];
        List<(Vector3 Position, Vector3 Power, float Range)> lights = [];
        for (int i = 0; i < asset.Entities.Length; i++)
        {
            await CollectAsync(asset.Entities[i], Matrix4x4.Identity, drawn, lights, cancel).ConfigureAwait(false);
            progress?.Report((i + 1f) / (asset.Entities.Length + 1)); // the last share is the writing
        }

        cancel.ThrowIfCancellationRequested();

        drawn.Sort((left, right) => right.Radius.CompareTo(left.Radius));

        // By size (which quarter of a doubling its radius is in) and material; and the biggest radius of each size.
        Dictionary<(int Size, Handle<Material> Material), (List<Vertex> Vertices, List<uint> Indices)> merged = [];
        Dictionary<int, float> biggest = [];
        int vertices = 0, indices = 0;
        foreach ((Mesh mesh, Matrix4x4 world, Handle<Material> material, float radius) in drawn)
        {
            if (radius <= 0f)
                continue;

            bool asBox = radius < BoxUnder;
            int adding = asBox ? 8 : mesh.Vertices.Length;
            int addingIndices = asBox ? 36 : mesh.Indices.Length;
            if (vertices + adding > MaxVertices || indices + addingIndices > MaxIndices)
                continue;

            (int Size, Handle<Material>) size = ((int)MathF.Floor(MathF.Log2(radius) * SizesPerDoubling), material);
            biggest[size.Size] = MathF.Max(radius, biggest.GetValueOrDefault(size.Size));
            if (!merged.TryGetValue(size, out (List<Vertex> Vertices, List<uint> Indices) into))
                merged[size] = into = ([], []);

            if (asBox)
                AddBox(mesh.Box, world, into.Vertices, into.Indices);
            else
                AddMesh(mesh, world, into.Vertices, into.Indices);

            vertices += adding;
            indices += addingIndices;
        }

        // One model, and one entity drawing it, per size and eight materials: a renderer has that many slots.
        List<Chunk.Entity> entities = Merged(lights);
        string key = Path.GetFileName(folder);
        int models = 0;
        foreach (KeyValuePair<(int Size, Handle<Material> Material), (List<Vertex> Vertices, List<uint> Indices)>[] group in merged
            .GroupBy(part => part.Key.Size)
            .OrderByDescending(size => size.Key)
            .SelectMany(size => size.Chunk(MaterialSlots.Capacity)))
        {
            Model model = new()
            {
                Meshes = [.. group.Select(part => new Mesh { Vertices = [.. part.Value.Vertices], Indices = [.. part.Value.Indices] })],
                Parts = [.. Enumerable.Range(0, group.Length).Select(index => new ModelPart(index, index))],
                SlotNames = [.. group.Select(part => part.Key.Material.Id.ToString())],
            };

            // The stand-in names its model by id, so the id is decided here and written in the model's sidecar.
            string name = $"standin{models}.model";
            ulong id = Math.Max(1, XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{key}/{name}")));
            Result written = await files.WriteBinaryAsync(files.Combine(folder, name), model.ToBytes(), cancel).ConfigureAwait(false);
            if (written.Ok)
                written = await files.WriteTextAsync(files.Combine(folder, name + ".meta"), $"{{\n    \"id\": {id},\n    \"version\": 1\n}}\n", cancel).ConfigureAwait(false);
            if (written.Failed)
                return Result<Dictionary<float, string>>.Failure(written.Message);

            MaterialSlots materials = default;
            for (int i = 0; i < group.Length; i++)
                materials[i] = group[i].Key.Material;

            entities.Add(new Chunk.Entity
            {
                Name = $"StandIn{models++}",
                Tags = ["static"],
                Components = new Dictionary<string, JsonElement>
                {
                    [nameof(Transform)] = JsonSerializer.SerializeToElement(Transform.Identity, AssetJson.Options),
                    [nameof(Renderer)] = JsonSerializer.SerializeToElement(new Renderer { Model = new Handle<Model>(id), Materials = materials, CullRadius = biggest[group[0].Key.Size] }, AssetJson.Options),
                },
            });
        }

        Chunk standIn = new() { Entities = [.. entities] };
        Result saved = await files.WriteTextAsync(files.Combine(folder, "standin.chunk"), JsonSerializer.Serialize(standIn, AssetJson.Options), cancel).ConfigureAwait(false);
        if (saved.Failed)
            return Result<Dictionary<float, string>>.Failure(saved.Message);

        progress?.Report(1f);

        return Result<Dictionary<float, string>>.Success(new() { [Distance] = "standin.chunk" });
    }

    /// <summary>
    /// Adds every part of every static renderer under <paramref name="entity"/>, placed by the transforms above it: the
    /// mesh of the model's lowest LOD when that has the model's parts, else the model's own. Every point and spot
    /// light under it goes into <paramref name="lights"/>, where the transforms put it.
    /// Nothing of an entity the chunk tags hidden, or of what is under it.
    /// </summary>
    private async Task CollectAsync(
        Chunk.Entity entity,
        Matrix4x4 parent,
        List<(Mesh Mesh, Matrix4x4 World, Handle<Material> Material, float Radius)> drawn,
        List<(Vector3 Position, Vector3 Power, float Range)> lights,
        CancellationToken cancel)
    {
        if (entity.Tags.Contains("hidden", StringComparer.OrdinalIgnoreCase))
            return;

        Matrix4x4 world = (Read<Transform>(entity) ?? Transform.Identity).Matrix * parent;
        AddLight(entity, world, lights);
        if (entity.Tags.Contains("static", StringComparer.OrdinalIgnoreCase) && Read<Renderer>(entity) is { } renderer)
        {
            Model? model = await assets.LoadAsync(renderer.Model, cancel: cancel).ConfigureAwait(false);
            (float Threshold, Handle<Model> Asset)[] lods = model is null ? [] : await assets.LodsAsync(renderer.Model, cancel: cancel).ConfigureAwait(false);
            Model? lowest = lods.Length > 0 ? await assets.LoadAsync(lods[^1].Asset, cancel: cancel).ConfigureAwait(false) : null;
            if (model is not null)
                Add(model, lowest is not null && lowest.Meshes.Length == model.Meshes.Length ? lowest : model, renderer, world, drawn);
        }

        foreach (Chunk.Entity child in entity.Children)
            await CollectAsync(child, world, drawn, lights, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The entity's point or spot light as where it is, the light it gives (colour times intensity) and its range.
    /// A spot gives its light into its cone only, so it counts for the share of all directions the cone is.
    /// </summary>
    private static void AddLight(Chunk.Entity entity, in Matrix4x4 world, List<(Vector3 Position, Vector3 Power, float Range)> lights)
    {
        if (Read<PointLight>(entity) is { } point)
            lights.Add((world.Translation, point.Color * point.Intensity, point.Range));
        else if (Read<SpotLight>(entity) is { } spot)
            lights.Add((world.Translation, spot.Color * spot.Intensity * ((1f - MathF.Cos(spot.OuterAngle * 0.5f)) * 0.5f), spot.Range));
    }

    /// <summary>
    /// The stand-in's lights, all static (nothing moves in a stand-in, so nothing recomputes where they are): a glow where each of the chunk's is, and the chunk's, gathered by where they stand on the ground into
    /// <see cref="LightsAcross"/> lots a side, and each lot one point light. It sits at the lot's middle (the brighter
    /// a light, the more it pulls), gives the lot's light added up, and reaches as far as the farthest reach of any
    /// of them.
    /// </summary>
    private static List<Chunk.Entity> Merged(List<(Vector3 Position, Vector3 Power, float Range)> lights)
    {
        List<Chunk.Entity> merged = [];
        lights.RemoveAll(light => Brightness(light.Power) <= 0f || light.Range <= 0f);
        if (lights.Count == 0)
            return merged;

        Vector3 min = lights[0].Position, max = lights[0].Position;
        foreach ((Vector3 position, _, _) in lights)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        // The lights themselves, as far as they can be seen from there: a dot where each is, or one dot for the
        // lights that stand close together, at their middle, in the colour and size of their light added up.
        foreach (IGrouping<(int, int, int), (Vector3 Position, Vector3 Power, float Range)> close in lights.GroupBy(light => GlowCell(light.Position)))
        {
            Vector3 power = Vector3.Zero, middle = Vector3.Zero;
            float weight = 0f;
            foreach ((Vector3 position, Vector3 lightPower, _) in close)
            {
                float brightness = Brightness(lightPower);
                power += lightPower;
                middle += position * brightness;
                weight += brightness;
            }

            float intensity = MathF.Max(power.X, MathF.Max(power.Y, power.Z));
            float radius = Math.Clamp(GlowRadius * MathF.Sqrt(intensity), GlowRadiusMin, GlowRadiusMax);
            merged.Add(new Chunk.Entity
            {
                Name = $"Glow{merged.Count}",
                Tags = ["static"],
                Components = new Dictionary<string, JsonElement>
                {
                    [nameof(Transform)] = JsonSerializer.SerializeToElement(Transform.Identity with { Position = middle / weight }, AssetJson.Options),
                    [nameof(Glow)] = JsonSerializer.SerializeToElement(new Glow(power / intensity * GlowBrightness, radius), AssetJson.Options),
                },
            });
        }

        int glows = merged.Count;

        Vector3 size = Vector3.Max(max - min, new Vector3(1e-3f));
        foreach (IGrouping<int, (Vector3 Position, Vector3 Power, float Range)> lot in lights.GroupBy(light => Lot(light.Position, min, size)).OrderBy(lot => lot.Key))
        {
            Vector3 power = Vector3.Zero, middle = Vector3.Zero;
            float weight = 0f;
            foreach ((Vector3 position, Vector3 lightPower, _) in lot)
            {
                float brightness = Brightness(lightPower);
                power += lightPower;
                middle += position * brightness;
                weight += brightness;
            }

            middle /= weight;
            float range = lot.Max(light => Vector3.Distance(light.Position, middle) + light.Range);
            float intensity = MathF.Max(power.X, MathF.Max(power.Y, power.Z));
            merged.Add(new Chunk.Entity
            {
                Name = $"Light{merged.Count - glows}",
                Tags = ["static"],
                Components = new Dictionary<string, JsonElement>
                {
                    [nameof(Transform)] = JsonSerializer.SerializeToElement(Transform.Identity with { Position = middle }, AssetJson.Options),
                    [nameof(PointLight)] = JsonSerializer.SerializeToElement(new PointLight(power / intensity, intensity, range), AssetJson.Options),
                },
            });
        }

        return merged;
    }

    /// <summary>
    /// The cube of <see cref="GlowSpacing"/> metres a side a position is in: lights in one cube share a glow.
    /// </summary>
    private static (int X, int Y, int Z) GlowCell(Vector3 position)
    {
        return ((int)MathF.Floor(position.X / GlowSpacing), (int)MathF.Floor(position.Y / GlowSpacing), (int)MathF.Floor(position.Z / GlowSpacing));
    }

    /// <summary>
    /// Which lot a light at <paramref name="position"/> is in: its square of the ground the lights stand on.
    /// </summary>
    private static int Lot(Vector3 position, Vector3 min, Vector3 size)
    {
        int x = Math.Min(LightsAcross - 1, (int)((position.X - min.X) / size.X * LightsAcross));
        int z = Math.Min(LightsAcross - 1, (int)((position.Z - min.Z) / size.Z * LightsAcross));
        return (z * LightsAcross) + x;
    }

    private static float Brightness(Vector3 power)
    {
        return power.X + power.Y + power.Z;
    }

    /// <summary>
    /// Every part of <paramref name="model"/>, drawn as the mesh <paramref name="shown"/> has for it.
    /// </summary>
    private static void Add(Model model, Model shown, in Renderer renderer, in Matrix4x4 world, List<(Mesh Mesh, Matrix4x4 World, Handle<Material> Material, float Radius)> drawn)
    {
        foreach (ModelPart part in model.Parts)
        {
            if (part.MeshIndex < 0 || part.MeshIndex >= shown.Meshes.Length)
                continue;

            Mesh mesh = shown.Meshes[part.MeshIndex];
            if (mesh.Indices.Length > 0)
                drawn.Add((mesh, world, renderer.Materials[Math.Clamp(part.MaterialSlot, 0, MaterialSlots.Capacity - 1)], mesh.Bounds.Transform(world).Radius));
        }
    }

    /// <summary>
    /// The entity's component of this type; null when it has none or its JSON is not one.
    /// </summary>
    private static T? Read<T>(Chunk.Entity entity) where T : struct
    {
        if (!entity.Components.TryGetValue(typeof(T).Name, out JsonElement json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, AssetJson.Options);
        }
        catch (JsonException)
        {
            return null; // the streaming system warns about it when the chunk itself spawns
        }
    }

    /// <summary>
    /// The mesh's box as eight vertices and twelve triangles in world space. The vertices carry no normal: a corner
    /// has no one normal, and a mesh without normals is shaded with each triangle's own (<c>GBuffer.frag.hlsl</c>),
    /// so the box is lit as a box for a third of the vertices flat faces of their own would take.
    /// </summary>
    private static void AddBox(in Aabb box, in Matrix4x4 world, List<Vertex> vertices, List<uint> indices)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = Vector3.Transform(new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z), world);

        Vector3 center = Vector3.Transform(box.Center, world);
        uint first = (uint)vertices.Count;
        foreach (Vector3 corner in corners)
            vertices.Add(new Vertex { Position = corner, Tangent = new Vector4(1, 0, 0, 1) });

        // Each face as two triangles; a triangle is clockwise from outside when cross(b - a, c - a) points away from the centre.
        ReadOnlySpan<byte> faces = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5];
        for (int face = 0; face < faces.Length; face += 4)
        {
            AddTriangle(corners, center, first, faces[face], faces[face + 1], faces[face + 2], indices);
            AddTriangle(corners, center, first, faces[face], faces[face + 2], faces[face + 3], indices);
        }
    }

    private static void AddTriangle(ReadOnlySpan<Vector3> corners, Vector3 center, uint first, int a, int b, int c, List<uint> indices)
    {
        Vector3 outward = ((corners[a] + corners[b] + corners[c]) / 3f) - center;
        bool flip = Vector3.Dot(Vector3.Cross(corners[b] - corners[a], corners[c] - corners[a]), outward) < 0f;
        indices.Add(first + (uint)a);
        indices.Add(first + (uint)(flip ? c : b));
        indices.Add(first + (uint)(flip ? b : c));
    }

    /// <summary>
    /// The mesh in world space; a mirroring transform turns its triangles round so they stay clockwise from outside.
    /// </summary>
    private static void AddMesh(Mesh mesh, in Matrix4x4 world, List<Vertex> vertices, List<uint> indices)
    {
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        Matrix4x4 normals = Matrix4x4.Transpose(inverse);
        bool mirrored = world.GetDeterminant() < 0f;
        uint first = (uint)vertices.Count;
        foreach (Vertex vertex in mesh.Vertices)
        {
            Vector3 tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), world);
            vertices.Add(new Vertex
            {
                Position = Vector3.Transform(vertex.Position, world),
                Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normals)),
                Tangent = new Vector4(tangent.LengthSquared() > 0f ? Vector3.Normalize(tangent) : Vector3.UnitX, mirrored ? -vertex.Tangent.W : vertex.Tangent.W),
                Uv = vertex.Uv,
            });
        }

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            indices.Add(first + mesh.Indices[i]);
            indices.Add(first + mesh.Indices[i + (mirrored ? 2 : 1)]);
            indices.Add(first + mesh.Indices[i + (mirrored ? 1 : 2)]);
        }
    }
}
