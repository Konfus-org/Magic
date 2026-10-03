using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Magic.IntegrationTests.Assets;

/// <summary>
/// The asset service over a temp Assets folder and the real file system and watcher. A test writes its files, then
/// <see cref="Open"/>s the service, so nothing waits on the watcher except the tests about it.
/// </summary>
public sealed class AssetsTests : IDisposable
{
    private static readonly Handle<Material> Mat = new(80);

    private readonly TempFolder _root = new();
    private readonly Events _events = new();
    private readonly Container _container = new();
    private readonly Project _project;

    public AssetsTests()
    {
        _project = new Project { Name = "Tests", Root = _root.Path, Resources = Path.Combine(_root.Path, "NoResources") };
        Directory.CreateDirectory(_project.Assets);
    }

    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void A_json_asset_is_read_from_its_file()
    {
        Write("M.mat", """{ "doubleSided": true }""", 80);
        using Services.Assets assets = Open();

        Material? material = assets.Load(Mat);

        Assert.True(material?.DoubleSided);
    }

    [Fact]
    public void An_asset_takes_its_id_from_its_sidecar()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();

        Material? material = assets.Load(Mat);

        Assert.Equal(80ul, material?.Id);
    }

    [Fact]
    public void An_asset_knows_its_path_under_the_assets_folder()
    {
        Write("Materials/M.mat", "{}", 80);
        using Services.Assets assets = Open();

        Material? material = assets.Load(Mat);

        Assert.Equal("Materials/M.mat", material?.Path);
    }

    [Fact]
    public void A_file_and_sidecar_may_start_with_a_bom()
    {
        File.WriteAllText(Path.Combine(_project.Assets, "M.mat"), """{ "doubleSided": true }""", Encoding.UTF8); // Encoding.UTF8 writes a BOM, as some editors do
        File.WriteAllText(Path.Combine(_project.Assets, "M.mat.meta"), """{ "id": 80 }""", Encoding.UTF8);
        using Services.Assets assets = Open();

        Material? material = assets.Load(Mat);

        Assert.NotNull(material);
    }

    [Fact]
    public void A_text_asset_is_the_text_of_its_file()
    {
        Write("S.vert.hlsl", "float4 main() { return 0; }", 81);
        using Services.Assets assets = Open();

        Shader? shader = assets.Load(new Handle<Shader>(81));

        Assert.Equal("float4 main() { return 0; }", shader?.Text);
    }

    [Fact]
    public void A_loader_in_the_container_fills_assets_of_its_type()
    {
        Write("T.png", "abc", 82);
        _container.Add<IAssetLoader<Texture>>(new LengthLoader());
        using Services.Assets assets = Open();

        Texture? texture = assets.Load(new Handle<Texture>(82));

        Assert.Equal(3, texture?.Width);
    }

    [Fact]
    public void Paths_lists_the_indexed_files_of_an_extension_in_path_order()
    {
        Write("Materials/B.mat", "{}", 80);
        Write("Materials/A.mat", "{}", 81);
        Write("S.vert.hlsl", "", 82);
        using Services.Assets assets = Open();

        string[] paths = assets.Paths(".mat");

        Assert.Equal(["Materials/A.mat", "Materials/B.mat"], paths);
    }

    [Fact]
    public void An_unknown_id_loads_as_null()
    {
        using Services.Assets assets = Open();

        Material? material = assets.Load(new Handle<Material>(1234567));

        Assert.Null(material);
    }

    [Fact]
    public void A_file_that_cannot_be_read_as_its_type_loads_as_null()
    {
        Write("M.mat", "{ not json", 80);
        using Services.Assets assets = Open();

        Material? material = assets.Load(Mat);

        Assert.Null(material);
    }

    [Fact]
    public void A_file_without_a_sidecar_gets_one()
    {
        _root.Write("Assets/M.mat", "{}");

        using Services.Assets assets = Open();

        Assert.True(File.Exists(Path.Combine(_project.Assets, "M.mat.meta")));
    }

    [Fact]
    public void Find_turns_a_path_into_a_handle()
    {
        Write("Materials/M.mat", "{}", 80);
        using Services.Assets assets = Open();

        Handle<Material> found = assets.Find<Material>("Materials/M.mat");

        Assert.Equal(Mat, found);
    }

    [Fact]
    public void Find_of_a_missing_path_is_none()
    {
        using Services.Assets assets = Open();

        Handle<Material> found = assets.Find<Material>("Materials/Missing.mat");

        Assert.Equal(Handle<Material>.None, found);
    }

    [Fact]
    public void Loads_of_one_asset_share_one_object()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();
        Material? first = assets.Load(Mat);

        Material? second = assets.Load(Mat);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Loads_of_one_asset_at_once_read_it_once()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => assets.LoadAsync(Mat)));

        Assert.Equal(1, Assert.Single(assets.PoolStats()).Misses);
    }

    [Fact]
    public void A_budget_of_zero_reads_the_file_every_time()
    {
        Write("M.mat", "{}", 80);
        _project.Settings.Assets.Budgets[nameof(Material)] = 0;
        using Services.Assets assets = Open();
        Material? first = assets.Load(Mat);

        Material? second = assets.Load(Mat);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Over_budget_the_least_recently_loaded_goes()
    {
        using Services.Assets assets = OpenWithBigMaterialsAndRoomForThree();
        assets.Load(new Handle<Material>(90));
        Material? oldest = assets.Load(new Handle<Material>(91));
        assets.Load(new Handle<Material>(92));
        assets.Load(new Handle<Material>(90)); // 90 is the most recent now, so 91 is the oldest

        assets.Load(new Handle<Material>(93));

        Assert.NotSame(oldest, assets.Load(new Handle<Material>(91)));
    }

    [Fact]
    public void Over_budget_a_recently_loaded_asset_stays()
    {
        using Services.Assets assets = OpenWithBigMaterialsAndRoomForThree();
        Material? recent = assets.Load(new Handle<Material>(90));
        assets.Load(new Handle<Material>(91));
        assets.Load(new Handle<Material>(92));
        assets.Load(new Handle<Material>(90));

        assets.Load(new Handle<Material>(93));

        Assert.Same(recent, assets.Load(new Handle<Material>(90)));
    }

    [Fact]
    public void Over_budget_the_pool_is_trimmed_below_its_budget()
    {
        for (int i = 0; i < 11; i++)
            Write($"Small{i}.mat", "{" + new string(' ', 100_000) + "}", 100 + (ulong)i); // about 0.1 MB of file each, which is what counts

        _project.Settings.Assets.Budgets[nameof(Material)] = 1; // room for ten
        using Services.Assets assets = Open();

        for (int i = 0; i < 11; i++)
            assets.Load(new Handle<Material>(100 + (ulong)i));

        Assert.Contains(assets.PoolStats(), pool => pool.Type == nameof(Material) && pool.Count == 9); // 0.9 MB or less
    }

    [Fact]
    public void An_asset_bigger_than_its_budget_is_still_kept_until_the_next_miss()
    {
        Write("Huge.mat", "{" + new string(' ', 1_200_000) + "}", 94); // about 1.2 MB of file
        _project.Settings.Assets.Budgets[nameof(Material)] = 1;
        using Services.Assets assets = Open();

        Material? huge = assets.Load(new Handle<Material>(94));

        Assert.Same(huge, assets.Load(new Handle<Material>(94)));
    }

    [Fact]
    public void A_load_with_dependencies_loads_what_the_asset_names()
    {
        Write("M.mat", """{ "shader": { "id": 81 } }""", 80);
        Write("S.surf.hlsl", "void surface() {}", 81);
        using Services.Assets assets = Open();

        assets.Load(Mat, dependencies: true);

        Assert.Contains(assets.PoolStats(), pool => pool.Type == nameof(Shader) && pool.Count == 1);
    }

    [Fact]
    public void A_load_without_dependencies_loads_the_asset_alone()
    {
        Write("M.mat", """{ "shader": { "id": 81 } }""", 80);
        Write("S.surf.hlsl", "void surface() {}", 81);
        using Services.Assets assets = Open();

        assets.Load(Mat);

        Assert.DoesNotContain(assets.PoolStats(), pool => pool.Type == nameof(Shader));
    }

    [Fact]
    public async Task An_async_load_with_dependencies_is_done_once_they_are_loaded()
    {
        Write("M.mat", """{ "shader": { "id": 81 } }""", 80);
        Write("S.surf.hlsl", "void surface() {}", 81);
        using Services.Assets assets = Open();

        await assets.LoadAsync(Mat, dependencies: true);

        Assert.Contains(assets.PoolStats(), pool => pool.Type == nameof(Shader) && pool.Count == 1);
    }

    [Fact]
    public async Task An_async_load_with_dependencies_ends_its_progress_at_one()
    {
        Write("M.mat", """{ "shader": { "id": 81 } }""", 80);
        Write("S.surf.hlsl", "void surface() {}", 81);
        using Services.Assets assets = Open();
        Told told = new();

        await assets.LoadAsync(Mat, dependencies: true, told);

        Assert.Equal(1f, told.Values[^1]);
    }

    [Fact]
    public void A_render_texture_a_material_names_is_not_loaded_as_a_texture()
    {
        Write("M.mat", """{ "params": { "color": { "texture": { "id": 83 } } } }""", 80);
        Write("Screen.rtex", "{}", 83);
        using Services.Assets assets = Open();

        assets.Load(Mat, dependencies: true);

        Assert.False(assets.HasFailed(new Handle<Texture>(83)));
    }

    [Fact]
    public async Task Loading_dependencies_loads_what_the_holders_name()
    {
        Write("S.surf.hlsl", "void surface() {}", 81);
        using Services.Assets assets = Open();

        await assets.LoadDependenciesAsync([new Material { Shader = new Handle<Shader>(81) }]);

        Assert.Contains(assets.PoolStats(), pool => pool.Type == nameof(Shader) && pool.Count == 1);
    }

    [Fact]
    public void An_asset_that_could_not_be_loaded_has_failed()
    {
        Write("M.mat", "{ not json", 80);
        using Services.Assets assets = Open();

        assets.Load(Mat);

        Assert.True(assets.HasFailed(Mat));
    }

    [Fact]
    public void A_failed_asset_is_not_kept_in_its_pool()
    {
        Write("M.mat", "{ not json", 80);
        using Services.Assets assets = Open();

        assets.Load(Mat);

        Assert.Equal(0, Assert.Single(assets.PoolStats()).Count);
    }

    [Fact]
    public async Task A_cancelled_load_throws()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        Func<Task> load = () => assets.LoadAsync(Mat, cancel: cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(load);
    }

    [Fact]
    public async Task A_load_one_caller_cancels_still_arrives_for_another()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();
        using CancellationTokenSource cancelled = new();
        Task<Material?> first = assets.LoadAsync(Mat, cancel: cancelled.Token);
        Task<Material?> second = assets.LoadAsync(Mat);

        await cancelled.CancelAsync();

        Assert.NotNull(await second);
    }

    [Fact]
    public async Task An_asset_whose_only_load_was_cancelled_loads_the_next_time()
    {
        Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assets.LoadAsync(Mat, cancel: cancelled.Token));

        Material? material = await assets.LoadAsync(Mat);

        Assert.NotNull(material);
    }

    [Fact]
    public void A_new_file_is_reported_as_added()
    {
        using Services.Assets assets = Open();

        _root.Write("Assets/New.mat", "{}");

        Assert.Equal(EventType.AssetAdded, NextEvent(assets).Type);
    }

    [Fact]
    public void A_new_file_can_be_loaded_once_it_is_reported()
    {
        using Services.Assets assets = Open();
        Write("Late.mat", "{}", 95);

        NextEvent(assets);

        Assert.NotNull(assets.Load(new Handle<Material>(95)));
    }

    [Fact]
    public void A_failed_load_is_tried_again_once_the_file_appears()
    {
        using Services.Assets assets = Open();
        assets.Load(new Handle<Material>(95));
        Write("Late.mat", "{}", 95);

        NextEvent(assets);

        Assert.NotNull(assets.Load(new Handle<Material>(95)));
    }

    [Fact]
    public void A_written_file_is_reported_as_modified()
    {
        string file = Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();

        File.WriteAllText(file, """{ "doubleSided": true }""");

        Assert.Equal(new Event(EventType.AssetModified, Id: 80, Text: "M.mat"), NextEvent(assets));
    }

    [Fact]
    public void A_written_sidecar_is_reported_as_its_asset_modified()
    {
        string file = Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();

        File.AppendAllText(file + ".meta", "\n");

        Assert.Equal(new Event(EventType.AssetModified, Id: 80, Text: "M.mat"), NextEvent(assets));
    }

    [Fact]
    public void A_modified_file_is_loaded_afresh()
    {
        string file = Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();
        Material? before = assets.Load(Mat);
        File.WriteAllText(file, """{ "doubleSided": true }""");

        NextEvent(assets);

        Assert.NotSame(before, assets.Load(Mat));
    }

    [Fact]
    public void A_moved_file_is_reported_as_moved_from_its_old_path()
    {
        string file = Write("M.mat", "{}", 80);
        Directory.CreateDirectory(Path.Combine(_project.Assets, "Sub"));
        using Services.Assets assets = Open();

        File.Move(file, Path.Combine(_project.Assets, "Sub", "Renamed.mat"));
        File.Move(file + ".meta", Path.Combine(_project.Assets, "Sub", "Renamed.mat.meta"));

        Assert.Equal(new Event(EventType.AssetMoved, Id: 80, Text: "Sub/Renamed.mat", OldText: "M.mat"), NextEvent(assets));
    }

    [Fact]
    public void A_renamed_folder_is_reported_as_its_assets_moved()
    {
        Write("Sub/M.mat", "{}", 80);
        using Services.Assets assets = Open();

        Directory.Move(Path.Combine(_project.Assets, "Sub"), Path.Combine(_project.Assets, "Other"));

        Assert.Equal(new Event(EventType.AssetMoved, Id: 80, Text: "Other/M.mat", OldText: "Sub/M.mat"), NextEvent(assets));
    }

    [Fact]
    public void A_deleted_file_is_reported_as_removed()
    {
        string file = Write("M.mat", "{}", 80);
        using Services.Assets assets = Open();

        File.Delete(file);
        File.Delete(file + ".meta");

        Assert.Equal(new Event(EventType.AssetRemoved, Id: 80, Text: "M.mat"), NextEvent(assets));
    }

    private Services.Assets Open()
    {
        return new Services.Assets(_project, new FileSystem(), _events, _container, new Threads());
    }

    /// <summary>
    /// Materials 90 to 93 of about 0.6 MB each in memory (300k characters), under a budget of 2 MB.
    /// </summary>
    private Services.Assets OpenWithBigMaterialsAndRoomForThree()
    {
        for (int i = 0; i < 4; i++)
            Write($"Big{i}.mat", "{" + new string(' ', 600_000) + "}", 90 + (ulong)i); // about 0.6 MB of file each

        _project.Settings.Assets.Budgets[nameof(Material)] = 2;

        return Open();
    }

    /// <summary>
    /// Writes a file under Assets and the sidecar giving it <paramref name="id"/>; returns the file's full path.
    /// </summary>
    private string Write(string relative, string text, ulong id)
    {
        _root.Write($"Assets/{relative}.meta", $$"""{ "id": {{id}} }""");

        return _root.Write($"Assets/{relative}", text);
    }

    /// <summary>
    /// Pumps the service like the frame loop until the watcher's changes have settled into exactly one event.
    /// </summary>
    private Event NextEvent(Services.Assets assets)
    {
        List<Event> seen = [];
        Stopwatch clock = Stopwatch.StartNew();
        while (seen.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(50);
            assets.ProcessChanges();
            seen.AddRange(_events.NextFrame());
        }

        Assert.True(seen.Count == 1, $"expected one event, got: {string.Join("; ", seen)}");
        return seen[0];
    }

    /// <summary>
    /// A texture loader that records how many bytes it was handed as the width.
    /// </summary>
    private sealed class LengthLoader : IAssetLoader<Texture>
    {
        public Result Load(Texture asset, byte[] bytes)
        {
            asset.Width = bytes.Length;

            return bytes.Length == 0 ? Result.Failure("the file is empty.") : Result.Success();
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
