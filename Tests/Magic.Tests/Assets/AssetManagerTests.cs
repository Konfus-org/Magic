using AssimpGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Services;
using SDLGem;
using SDLImageGem;
using SDLTtfGem;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Xunit;

namespace Magic.Tests.Assets;

/// <summary>
/// Runs the manager over the repo's Resources folder plus a temp assets folder, with the loader gems
/// constructed in-process (no gem loader involved). One class, so the static loader registry is only
/// touched by one test at a time.
/// </summary>
public sealed class AssetManagerTests : IDisposable
{
    // Ids from the sidecars in Resources/.
    private static readonly Handle<Material> Checker = new(7381546209823741553);
    private static readonly Handle<Texture> Checkerboard = new(36);
    private static readonly Handle<Model> Cube = new(514);
    private static readonly Handle<Model> Plane = new(519);
    private static readonly Handle<Shader> MeshVertex = new(3645401862674444787);

    // A temp project root; its Assets folder is what the tests fill (Project derives Assets and Cache from Root).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MagicTests", Guid.NewGuid().ToString("N"));
    private readonly string _temp;
    private readonly Project _project;
    private readonly Sdl _sdl;
    private readonly SdlImage _image = new();
    private readonly SdlTtf _ttf;
    private readonly AssimpModels _models;
    private readonly EventBus _events = new();
    private readonly AssetManager _assets;

    public AssetManagerTests()
    {
        // The repo root is stamped into Magic.dll; the assets root is a temp folder this test fills.
        string root = typeof(Project).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MagicRoot").Value!;
        _project = new Project { Name = "Tests", Root = _root, Gems = AppContext.BaseDirectory, Resources = Path.Combine(root, "Resources") };
        _temp = _project.Assets;
        Directory.CreateDirectory(_temp);

        _sdl = new Sdl(_project, new SystemScheduler());
        _ttf = new SdlTtf();
        _models = new AssimpModels(_project);
        AssetLoaderRegistry.Register(typeof(Texture), _image);
        AssetLoaderRegistry.Register(typeof(Font), _ttf);
        AssetLoaderRegistry.Register(typeof(Model), _models);

        File.Copy(@"C:\Windows\Fonts\arial.ttf", Path.Combine(_temp, "Arial.ttf"));
        File.WriteAllText(Path.Combine(_temp, "Arial.ttf.meta"), """{ "id": 77, "version": 1, "size": 24 }""");
        File.WriteAllText(Path.Combine(_temp, "Broken.mat"), "{ not json");
        File.WriteAllText(Path.Combine(_temp, "Broken.mat.meta"), """{ "id": 78 }""");
        File.WriteAllText(Path.Combine(_temp, "NoSidecar.mat"), "{}");
        File.WriteAllText(Path.Combine(_temp, "Tri.obj"), string.Join('\n', "v 0 0 0", "v 1 0 0", "v 0 1 0", "vn 0 0 1", "vn 0 0 1", "vn 0 0 1", "f 1//1 2//2 3//3", ""));
        File.WriteAllText(Path.Combine(_temp, "Tri.obj.meta"), """{ "id": 79 }""");

        _assets = new AssetManager(_project, new FileSystem(), _events);
    }

    public void Dispose()
    {
        _assets.Dispose();
        AssetLoaderRegistry.Unregister(typeof(Texture), _image);
        AssetLoaderRegistry.Unregister(typeof(Font), _ttf);
        AssetLoaderRegistry.Unregister(typeof(Model), _models);
        _ttf.Dispose();
        _sdl.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Json_asset_takes_the_file_and_its_sidecar()
    {
        Material? material = await _assets.LoadAsync(Checker);

        Assert.NotNull(material);
        Assert.Equal(Checker.Id, material.Id);
        Assert.Equal(1, material.Version);
        Assert.Equal("Materials/Checker.mat", material.Path);
        Assert.Equal("Checker", material.Name);
        Assert.Equal(0.8f, material.Roughness);
        Assert.Equal(Vector4.One, material.Color);
        Assert.Equal(Checkerboard, material.ColorTexture);
        Assert.Equal(MaterialType.Opaque, material.Type);
        Assert.Equal("Materials/Checker.mat", _assets.PathOf(Checker.Id));
        Assert.NotSame(material, await _assets.LoadAsync(Checker)); // nothing is kept
    }

    [Fact]
    public async Task Text_asset_reads_the_file_and_derives_stage_and_includes()
    {
        Shader? shader = await _assets.LoadAsync(MeshVertex);

        Assert.NotNull(shader);
        Assert.Equal(ShaderStage.Vertex, shader.Stage);
        Assert.Contains("Include/Bindings.hlsli", shader.Includes);
        Assert.Contains("Include/Frame.hlsli", shader.Includes);
        Assert.StartsWith("//", shader.Text.TrimStart());
    }

    [Fact]
    public async Task Texture_loads_through_the_sdl_image_gem_with_mips()
    {
        double last = 0;
        Progress<double> progress = new(p => last = Math.Max(last, p));
        Texture? texture = await _assets.LoadAsync(Checkerboard, progress);

        Assert.NotNull(texture);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(TextureFormat.Rgba8Srgb, texture.Format);
        Assert.Equal(TextureWrap.Repeat, texture.Wrap);
        Assert.Equal(TextureChannels.Rgb, texture.Channels);
        Assert.True(texture.Mipmaps);
        Assert.Equal(4, texture.Levels.Length); // 8, 4, 2, 1
        Assert.Equal(new TextureLevel(1, 1, 8 * 8 * 4 + 4 * 4 * 4 + 2 * 2 * 4, 4), texture.Levels[^1]);
        Assert.Equal(texture.Levels.Sum(l => l.Size), texture.Pixels.Length);
        Assert.Equal(255, texture.Pixels[3]); // opaque
        await Task.Delay(50); // Progress<T> posts to the pool
        Assert.Equal(1, last);
    }

    [Fact]
    public async Task Model_loads_through_assimp_in_engine_space()
    {
        Model? cube = await _assets.LoadAsync(Cube);

        Assert.NotNull(cube);
        Mesh mesh = Assert.Single(cube.Meshes);
        Assert.Equal(36, mesh.Indices.Length);
        Assert.Single(cube.Parts);
        Assert.Single(cube.SlotNames);
        foreach (Vertex v in mesh.Vertices)
        {
            Assert.Equal(1f, MathF.Abs(v.Position.X), 3);
            Assert.Equal(1f, MathF.Abs(v.Position.Y), 3);
            Assert.Equal(1f, MathF.Abs(v.Position.Z), 3);
            Assert.Equal(1f, v.Normal.Length(), 3);
        }
        // Clockwise from outside: the geometric normal of every triangle points the way its vertex normals do.
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vertex a = mesh.Vertices[mesh.Indices[i]], b = mesh.Vertices[mesh.Indices[i + 1]], c = mesh.Vertices[mesh.Indices[i + 2]];
            Vector3 face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Assert.True(Vector3.Dot(face, a.Normal + b.Normal + c.Normal) > 0);
        }

        // Blender's Z up became Y up: the plane lies flat and faces up.
        Model? plane = await _assets.LoadAsync(Plane);
        Assert.NotNull(plane);
        foreach (Vertex v in plane.Meshes[0].Vertices)
        {
            Assert.Equal(0f, v.Position.Y, 3);
            Assert.Equal(1f, v.Normal.Y, 3);
        }
    }

    [Fact]
    public void Find_turns_a_path_into_a_handle()
    {
        Assert.Equal(Checkerboard, _assets.Find<Texture>("Textures/Checkerboard.png"));
        Assert.Equal(Checker, _assets.Find<Material>("Materials/Checker.mat"));
        Assert.Equal(new Handle<Font>(77), _assets.Find<Font>("Arial.ttf")); // the project's Assets folder
        Assert.Equal(Handle<Texture>.None, _assets.Find<Texture>("Textures/Missing.png"));

        // The engine icon is a vector image, rasterised at the size its sidecar asks for.
        Handle<Texture> mage = _assets.Find<Texture>("Icons/Mage.svg");
        Assert.True(mage.IsValid);
        Texture? icon = _assets.Load(mage);
        Assert.NotNull(icon);
        Assert.Equal(256, icon.Width);
        Assert.Equal(256, icon.Height);
        Assert.Contains(icon.Pixels.Take(icon.Levels[0].Size).Where((_, i) => i % 4 == 3), a => a == 255); // something opaque was drawn
    }

    [Fact]
    public void Synchronous_load_gives_the_same_assets()
    {
        Material? material = _assets.Load(Checker);
        Assert.NotNull(material);
        Assert.Equal(0.8f, material.Roughness);
        Assert.Equal(MaterialType.Opaque, material.Type);

        Shader? shader = _assets.Load(MeshVertex);
        Assert.NotNull(shader);
        Assert.Equal(ShaderStage.Vertex, shader.Stage);

        Texture? texture = _assets.Load(Checkerboard);
        Assert.NotNull(texture);
        Assert.Equal(4, texture.Levels.Length);

        Model? cube = _assets.Load(Cube);
        Assert.NotNull(cube);
        Assert.Equal(36, cube.Meshes[0].Indices.Length);

        Font? font = _assets.Load(new Handle<Font>(77));
        Assert.NotNull(font);
        Assert.True(font.Glyphs['A'].Width > 0);

        Assert.Null(_assets.Load(new Handle<Material>(1234567)));
        Assert.Null(_assets.Load(new Handle<Material>(78)));
    }

    [Fact]
    public async Task Non_fbx_models_are_already_in_metres()
    {
        // Tri.obj (written by the constructor) is a right-handed triangle facing +Z, one metre on a side; OBJ has no unit scale to fold in.
        Model? model = await _assets.LoadAsync(new Handle<Model>(79));

        Assert.NotNull(model);
        Mesh mesh = Assert.Single(model.Meshes);
        Vector3[] positions = [.. mesh.Vertices.Select(v => v.Position)];
        Assert.Contains(new Vector3(-1, 0, 0), positions); // X mirrored, not scaled
        Assert.Contains(new Vector3(0, 1, 0), positions);
        Vertex a = mesh.Vertices[mesh.Indices[0]], b = mesh.Vertices[mesh.Indices[1]], c = mesh.Vertices[mesh.Indices[2]];
        Assert.Equal(1f, Vector3.Dot(Vector3.Normalize(Vector3.Cross(b.Position - a.Position, c.Position - a.Position)), a.Normal), 3);
    }

    [Fact]
    public async Task Font_loads_through_sdl_ttf_as_an_atlas()
    {
        Font? font = await _assets.LoadAsync(new Handle<Font>(77));

        Assert.NotNull(font);
        Assert.Equal(24f, font.Size);
        Assert.True(font.LineHeight > 0);
        Assert.True(font.Ascent > 0);
        Assert.Equal(font.Width * font.Height * 4, font.Pixels.Length);
        Assert.True(BitOperations.IsPow2(font.Width) && BitOperations.IsPow2(font.Height));
        Glyph a = font.Glyphs['A'];
        Assert.True(a.Width > 0 && a.Height > 0 && a.Advance > 0);
        Assert.True(a.X + a.Width <= font.Width && a.Y + a.Height <= font.Height);
        Assert.Contains(font.Pixels.Skip((a.Y * font.Width + a.X) * 4).Take(a.Width * 4), b => b != 0);
        Assert.True(font.Glyphs['i'].Width < font.Glyphs['W'].Width);
    }

    [Fact]
    public async Task Failures_are_null()
    {
        Assert.Null(await _assets.LoadAsync(new Handle<Material>(1234567)));
        Assert.Null(_assets.PathOf(1234567));
        Assert.Null(await _assets.LoadAsync(new Handle<Material>(78))); // not JSON
        Assert.Null(await _assets.LoadAsync(new Handle<Texture>(Checker.Id))); // wrong type: a .mat through the image loader
    }

    [Fact]
    public void A_file_without_a_sidecar_gets_one()
    {
        string meta = File.ReadAllText(Path.Combine(_temp, "NoSidecar.mat.meta"));
        Assert.Contains("\"id\":", meta);
        Assert.Contains("\"version\": 1", meta);
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _assets.LoadAsync(Cube, cancellationToken: cancel.Token));
        Assert.NotNull(await _assets.LoadAsync(Cube));
    }

    [Fact]
    public void Watching_reports_added_modified_moved_and_removed()
    {
        List<object> seen = [];
        using IDisposable a = _events.Subscribe<AssetAdded>(e => seen.Add(e));
        using IDisposable m = _events.Subscribe<AssetModified>(e => seen.Add(e));
        using IDisposable v = _events.Subscribe<AssetMoved>(e => seen.Add(e));
        using IDisposable r = _events.Subscribe<AssetRemoved>(e => seen.Add(e));

        // Added: a new file gets a sidecar and an id.
        string file = Path.Combine(_temp, "New.mat");
        File.WriteAllText(file, "{}");
        AssetAdded added = Assert.IsType<AssetAdded>(Next(seen));
        Assert.Equal("New.mat", added.Path);
        Assert.True(File.Exists(file + ".meta"));
        Assert.Equal("New.mat", _assets.PathOf(added.Id));

        // Modified: the file is written again.
        File.WriteAllText(file, """{ "roughness": 0.1 }""");
        Assert.Equal(new AssetModified(added.Id, "New.mat"), Next(seen));

        // Modified: the sidecar is written.
        File.AppendAllText(file + ".meta", "\n");
        Assert.Equal(new AssetModified(added.Id, "New.mat"), Next(seen));

        // Moved: file and sidecar go to a subfolder, same id.
        Directory.CreateDirectory(Path.Combine(_temp, "Sub"));
        string moved = Path.Combine(_temp, "Sub", "Renamed.mat");
        File.Move(file, moved);
        File.Move(file + ".meta", moved + ".meta");
        Assert.Equal(new AssetMoved(added.Id, "New.mat", "Sub/Renamed.mat"), Next(seen));
        Assert.Equal("Sub/Renamed.mat", _assets.PathOf(added.Id));

        // Moved: the whole folder is renamed; only the folder is reported.
        Directory.Move(Path.Combine(_temp, "Sub"), Path.Combine(_temp, "Other"));
        Assert.Equal(new AssetMoved(added.Id, "Sub/Renamed.mat", "Other/Renamed.mat"), Next(seen));

        // Removed.
        File.Delete(Path.Combine(_temp, "Other", "Renamed.mat"));
        File.Delete(Path.Combine(_temp, "Other", "Renamed.mat.meta"));
        Assert.Equal(new AssetRemoved(added.Id, "Other/Renamed.mat"), Next(seen));
        Assert.Null(_assets.PathOf(added.Id));
    }

    /// <summary>Pumps the manager like the main loop until one event has arrived.</summary>
    private object Next(List<object> seen)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (seen.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(50);
            _assets.ProcessChanges();
        }
        Assert.True(seen.Count == 1, $"expected one event, got: {string.Join("; ", seen)}");
        object e = seen[0];
        seen.Clear();
        return e;
    }
}
