using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using Xunit;

namespace Magic.IntegrationTests.Assets;

/// <summary>
/// The lesser versions of an asset, over a temp Assets folder and a temp cache: the ones a sidecar names, and the
/// ones a generator in the container makes. Materials stand for any asset type; the generator here writes two
/// materials and counts how often it was asked.
/// </summary>
public sealed class AssetLodsTests : IDisposable
{
    private static readonly Handle<Material> Mat = new(80);

    private readonly TempFolder _root = new();
    private readonly Container _container = new();
    private readonly Project _project;

    public AssetLodsTests()
    {
        _project = new Project { Name = "Tests", Root = _root.Path, Resources = Path.Combine(_root.Path, "NoResources"), Cache = Path.Combine(_root.Path, "Cache") };
        Directory.CreateDirectory(_project.Assets);
    }

    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void An_asset_with_no_lods_and_no_generator_has_none()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        using Services.Assets assets = Open();

        (float, Handle<Material>)[] lods = assets.Lods(Mat);

        Assert.Empty(lods);
    }

    [Fact]
    public void The_lods_a_sidecar_names_are_the_assets()
    {
        Write("M.mat", "{}", """{ "id": 80, "lods": { "0.5": 81 } }""");
        using Services.Assets assets = Open();

        (float, Handle<Material>)[] lods = assets.Lods(Mat);

        Assert.Equal([(0.5f, new Handle<Material>(81))], lods);
    }

    [Fact]
    public void The_lod_with_the_highest_threshold_comes_first()
    {
        Write("M.mat", "{}", """{ "id": 80, "lods": { "0.1": 82, "0.5": 81 } }""");
        using Services.Assets assets = Open();

        (float Threshold, Handle<Material>)[] lods = assets.Lods(Mat);

        Assert.Equal([0.5f, 0.1f], lods.Select(lod => lod.Threshold));
    }

    [Fact]
    public void An_asset_with_lods_of_its_own_is_not_given_to_the_generator()
    {
        Write("M.mat", "{}", """{ "id": 80, "lods": { "0.5": 81 } }""");
        TwoMaterials generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using Services.Assets assets = Open();

        assets.Lods(Mat);

        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public void A_generator_makes_the_lods_of_an_asset_that_has_none()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using Services.Assets assets = Open();

        (float Threshold, Handle<Material>)[] lods = assets.Lods(Mat);

        Assert.Equal([0.5f, 0.1f], lods.Select(lod => lod.Threshold));
    }

    [Fact]
    public void A_generated_lod_loads_like_any_asset()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using Services.Assets assets = Open();

        Material? lod = assets.Load(assets.Lods(Mat)[0].Asset);

        Assert.True(lod?.DoubleSided);
    }

    [Fact]
    public void A_generated_lod_has_no_lods_of_its_own()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using Services.Assets assets = Open();

        (float, Handle<Material>)[] lods = assets.Lods(assets.Lods(Mat)[0].Asset);

        Assert.Empty(lods);
    }

    [Fact]
    public void Lods_made_once_are_found_in_the_cache_the_next_run()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        TwoMaterials generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using (Services.Assets first = Open())
            first.Lods(Mat);

        using Services.Assets second = Open();
        second.Lods(Mat);

        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public void Lods_found_in_the_cache_are_the_ones_that_were_made()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        (float, Handle<Material>)[] made;
        using (Services.Assets first = Open())
            made = first.Lods(Mat);

        using Services.Assets second = Open();
        (float, Handle<Material>)[] found = second.Lods(Mat);

        Assert.Equal(made, found);
    }

    [Fact]
    public void An_asset_whose_file_changed_has_its_lods_made_again()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        TwoMaterials generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using (Services.Assets first = Open())
            first.Lods(Mat);

        Write("M.mat", """{ "doubleSided": true }""", """{ "id": 80 }""");
        using Services.Assets second = Open();
        second.Lods(Mat);

        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public void An_asset_whose_sidecar_changed_has_its_lods_made_again()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        TwoMaterials generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using (Services.Assets first = Open())
            first.Lods(Mat);

        Write("M.mat", "{}", """{ "id": 80, "version": 2 }""");
        using Services.Assets second = Open();
        second.Lods(Mat);

        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public void Lods_made_of_another_asset_are_made_again_when_that_one_changed()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        Write("Other.mat", "{}", """{ "id": 81 }""");
        LoadsAnother generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using (Services.Assets first = Open())
        {
            generator.Assets = first;
            first.Lods(Mat);
        }

        Write("Other.mat", """{ "doubleSided": true }""", """{ "id": 81 }""");
        using Services.Assets second = Open();
        generator.Assets = second;
        second.Lods(Mat);

        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public void Lods_made_of_another_asset_are_found_in_the_cache_while_that_one_is_the_same()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        Write("Other.mat", "{}", """{ "id": 81 }""");
        LoadsAnother generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using (Services.Assets first = Open())
        {
            generator.Assets = first;
            first.Lods(Mat);
        }

        using Services.Assets second = Open();
        generator.Assets = second;
        second.Lods(Mat);

        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public void The_lods_made_of_an_earlier_file_are_deleted()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using (Services.Assets first = Open())
            first.Lods(Mat);

        Write("M.mat", """{ "doubleSided": true }""", """{ "id": 80 }""");
        using Services.Assets second = Open();
        second.Lods(Mat);

        Assert.Single(Directory.GetDirectories(Path.Combine(_project.Cache, "Lods", nameof(Material))));
    }

    [Fact]
    public void The_lods_of_an_asset_that_is_gone_are_deleted()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        Write("Gone.mat", "{}", """{ "id": 81 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using (Services.Assets first = Open())
        {
            first.Lods(Mat);
            first.Lods(new Handle<Material>(81));
        }

        File.Delete(Path.Combine(_project.Assets, "Gone.mat"));
        File.Delete(Path.Combine(_project.Assets, "Gone.mat.meta"));
        using Services.Assets second = Open();
        second.Lods(Mat);

        Assert.Single(Directory.GetDirectories(Path.Combine(_project.Cache, "Lods", nameof(Material))));
    }

    [Fact]
    public void A_generator_that_fails_leaves_the_asset_without_lods()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new Failing());
        using Services.Assets assets = Open();

        (float, Handle<Material>)[] lods = assets.Lods(Mat);

        Assert.Empty(lods);
    }

    [Fact]
    public async Task Cancelling_stops_the_generator()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        StallsOnce generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using Services.Assets assets = Open();
        using CancellationTokenSource cancel = new();
        Task finding = assets.LodsAsync(Mat, cancel: cancel.Token);
        await generator.Stalled;

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finding);
    }

    [Fact]
    public async Task Lods_whose_generation_was_cancelled_are_generated_when_asked_for_again()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        StallsOnce generator = new();
        _container.Add<ILODGenerator<Material>>(generator);
        using Services.Assets assets = Open();
        using CancellationTokenSource cancel = new();
        Task finding = assets.LodsAsync(Mat, cancel: cancel.Token);
        await generator.Stalled;
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finding);

        (float, Handle<Material>)[] lods = await assets.LodsAsync(Mat);

        Assert.Single(lods);
    }

    [Fact]
    public async Task The_generator_tells_the_progress_of_whoever_asked()
    {
        Write("M.mat", "{}", """{ "id": 80 }""");
        _container.Add<ILODGenerator<Material>>(new TwoMaterials());
        using Services.Assets assets = Open();
        Told told = new();

        await assets.LodsAsync(Mat, told);

        Assert.Equal([0.5f, 1f], told.Values);
    }

    private Services.Assets Open()
    {
        return new Services.Assets(_project, new FileSystem(), new Events(), _container, new Threads());
    }

    private void Write(string file, string content, string meta)
    {
        _root.Write($"Assets/{file}", content);
        _root.Write($"Assets/{file}.meta", meta);
    }

    /// <summary>
    /// Two double-sided materials, for half and a tenth.
    /// </summary>
    private sealed class TwoMaterials : ILODGenerator<Material>
    {
        public int Calls { get; private set; }

        public int Version => 1;

        public async Task<Result<Dictionary<float, string>>> GenerateAsync(Material asset, string folder, IProgress<float>? progress, CancellationToken cancel)
        {
            Calls++;
            progress?.Report(0.5f);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "half.mat"), """{ "doubleSided": true }""", cancel);
            await File.WriteAllTextAsync(Path.Combine(folder, "tenth.mat"), """{ "doubleSided": true }""", cancel);

            return Result<Dictionary<float, string>>.Success(new() { [0.1f] = "tenth.mat", [0.5f] = "half.mat" });
        }
    }

    /// <summary>
    /// One material for half, made after loading material 81 through <see cref="Assets"/>.
    /// </summary>
    private sealed class LoadsAnother : ILODGenerator<Material>
    {
        public Services.Assets? Assets { get; set; }

        public int Calls { get; private set; }

        public int Version => 1;

        public async Task<Result<Dictionary<float, string>>> GenerateAsync(Material asset, string folder, IProgress<float>? progress, CancellationToken cancel)
        {
            Calls++;
            if (Assets is not null)
                await Assets.LoadAsync(new Handle<Material>(81), cancel: cancel);

            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "half.mat"), "{}", cancel);

            return Result<Dictionary<float, string>>.Success(new() { [0.5f] = "half.mat" });
        }
    }

    /// <summary>
    /// Never done the first time it is asked, until that is cancelled; one material for half after that.
    /// </summary>
    private sealed class StallsOnce : ILODGenerator<Material>
    {
        private readonly TaskCompletionSource _stalled = new();
        private int _calls;

        /// <summary>
        /// Done once the first call is waiting to be cancelled.
        /// </summary>
        public Task Stalled => _stalled.Task;

        public int Version => 1;

        public async Task<Result<Dictionary<float, string>>> GenerateAsync(Material asset, string folder, IProgress<float>? progress, CancellationToken cancel)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _stalled.SetResult();
                await Task.Delay(Timeout.Infinite, cancel);
            }

            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "half.mat"), "{}", cancel);

            return Result<Dictionary<float, string>>.Success(new() { [0.5f] = "half.mat" });
        }
    }

    private sealed class Failing : ILODGenerator<Material>
    {
        public int Version => 1;

        public Task<Result<Dictionary<float, string>>> GenerateAsync(Material asset, string folder, IProgress<float>? progress, CancellationToken cancel)
        {
            return Task.FromResult(Result<Dictionary<float, string>>.Failure("on purpose"));
        }
    }

    /// <summary>
    /// Keeps what it is told, in order.
    /// </summary>
    private sealed class Told : IProgress<float>
    {
        public List<float> Values { get; } = [];

        public void Report(float value)
        {
            Values.Add(value);
        }
    }
}
