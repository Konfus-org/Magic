using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The files with a sidecar that each asset directory holds, read once and then kept up to date from the changes that
/// the watches see, so that a new <see cref="AssetIndex"/> reads no directory again.
/// </summary>
/// <remarks>
/// Any thread.
/// </remarks>
internal sealed class AssetScans(IFileSystem files)
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, ImmutableArray<IndexedFile>> _scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="directory"/> and keeps it, replacing what was kept for it.
    /// </summary>
    /// <remarks>
    /// A sidecar without its file is warned about, and a broken one is an error and gives no asset. The sidecars are
    /// read in parallel.
    /// </remarks>
    public void Scan(string directory)
    {
        ImmutableArray<IndexedFile> found = [];
        Result<string[]> sidecars = files.EnumerateFiles(directory, "*" + AssetIndex.Sidecar, recursive: true);
        if (sidecars.Ok)
        {
            IndexedFile?[] read = new IndexedFile?[sidecars.Value.Length];
            Parallel.For(0, read.Length, i => read[i] = ReadOne(directory, sidecars.Value[i]));
            ImmutableArray<IndexedFile>.Builder kept = ImmutableArray.CreateBuilder<IndexedFile>(read.Length);
            foreach (IndexedFile? one in read)
            {
                if (one is not null)
                    kept.Add(one);
            }

            // The order of a directory is its files' paths, as in Refresh, so that which of two ids wins does not depend
            // on the listing or on a refresh.
            kept.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            found = kept.ToImmutable();
        }
        else
        {
            Log.Error($"The assets under {directory} could not be listed: {sidecars.Error}");
        }

        lock (_gate)
        {
            _scans[directory] = found;
        }
    }

    /// <summary>
    /// The files of <paramref name="directory"/> as last read, empty for a directory that was not.
    /// </summary>
    public ImmutableArray<IndexedFile> Get(string directory)
    {
        lock (_gate)
        {
            return _scans.TryGetValue(directory, out ImmutableArray<IndexedFile> found) ? found : [];
        }
    }

    /// <summary>
    /// Reads again only <paramref name="touched"/>, paths from their asset directories, in each of
    /// <paramref name="directories"/> that was scanned: what is gone leaves, and what is there is read.
    /// </summary>
    /// <remarks>
    /// A touched path can be a file, its sidecar (counted as the file) or a folder, which stands for what is under it.
    /// </remarks>
    public void Refresh(ImmutableArray<string> directories, IReadOnlySet<string> touched)
    {
        lock (_gate)
        {
            foreach (string directory in directories)
            {
                if (!_scans.TryGetValue(directory, out ImmutableArray<IndexedFile> before))
                    continue;

                List<IndexedFile> now = [];
                foreach (IndexedFile file in before)
                {
                    if (!IsUnder(file.Path, touched))
                        now.Add(file);
                }

                foreach (string path in touched)
                {
                    string full = $"{directory}/{path}";
                    if (files.FileExists(full + AssetIndex.Sidecar))
                    {
                        if (ReadOne(directory, full + AssetIndex.Sidecar) is { } read)
                            now.Add(read);
                    }
                    else if (files.DirectoryExists(full))
                    {
                        Result<string[]> sidecars = files.EnumerateFiles(full, "*" + AssetIndex.Sidecar, true);
                        foreach (string sidecar in sidecars.Ok ? sidecars.Value : [])
                        {
                            if (ReadOne(directory, sidecar) is { } read)
                                now.Add(read);
                        }
                    }
                }

                // The order of a directory is its files' paths, so that which of two ids wins does not depend on
                // when it was read.
                now.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
                _scans[directory] = [.. now];
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of <paramref name="paths"/>, or under one.
    /// </summary>
    private static bool IsUnder(string path, IReadOnlySet<string> paths)
    {
        if (paths.Contains(path))
            return true;

        for (int slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (paths.Contains(path[..slash]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The file that <paramref name="sidecar"/> makes an asset, or null (with a log) when it is not one.
    /// </summary>
    private IndexedFile? ReadOne(string directory, string sidecar)
    {
        string file = sidecar[..^AssetIndex.Sidecar.Length];
        string path = file[(directory.Length + 1)..].Replace('\\', '/');
        if (!files.FileExists(file))
        {
            Log.Warn($"{path}{AssetIndex.Sidecar} has no {path} beside it.");
            return null;
        }

        Result<IndexedFile> read = AssetIndex.ReadSidecar(files, sidecar, path, file);
        if (!read.Ok)
        {
            Log.Error($"{path}{AssetIndex.Sidecar} is not a sidecar, so {path} is not an asset: {read.Error}");
            return null;
        }

        return read.Value;
    }
}
