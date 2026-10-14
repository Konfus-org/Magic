using System.IO;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.PipelineLoader;

/// <summary>
/// Pipelines as they load: the post-processor that gives every loaded <see cref="Pipeline"/> its
/// <see cref="Pipeline.Passes"/>: each entry in order, a nested pipeline's passes in its place, each pass with its params
/// merged and what the settings turn off left out. What it loads to make
/// one, its passes and nested pipelines, is what the assets make it from again when one of them changes.
/// </summary>
internal sealed class PipelineLoaderGem(Assets assets, PipelineSettings settings) : IGem, ILoadPostProcessor<Pipeline>
{
    public async Task<Result<Pipeline>> ProcessAsync(Pipeline asset, ulong version, CancellationToken cancel)
    {
        List<PipelinePass> passes = [];
        foreach (PipelineEntry entry in asset.Entries)
        {
            bool isPass = entry.Pass != Handle<Pass>.None;
            string name = Path.GetFileNameWithoutExtension((isPass ? assets.Path(entry.Pass) : assets.Path(entry.Pipeline)) ?? string.Empty);
            if (IsOff(name))
            {
                continue;
            }

            if (isPass)
            {
                Result<Pass> pass = await assets.LoadAsync(entry.Pass, cancel).ConfigureAwait(false);
                if (pass.Ok)
                {
                    passes.Add(new PipelinePass(pass.Value, name, Merge(name, entry, pass.Value.Params)));
                }
                else
                {
                    Debugging.Error($"Pass {name} does not run: {pass.Error}");
                }

                continue;
            }

            Result<Pipeline> nested = await assets.LoadAsync(entry.Pipeline, cancel).ConfigureAwait(false);
            if (!nested.Ok)
            {
                Debugging.Error($"Pipeline {name} does not load, so its passes do not run: {nested.Error}");
                continue;
            }

            foreach (PipelinePass pass in nested.Value.Passes)
            {
                passes.Add(pass with { Params = Merge(pass.Name, entry, pass.Params) });
            }
        }

        return Result.Success(new Pipeline { Entries = asset.Entries, Passes = passes });
    }

    /// <summary>
    /// Pass <paramref name="name"/>'s params: the settings' first, then <paramref name="entry"/>'s, then
    /// <paramref name="inner"/>, the pass's own or what a nested pipeline made of them, the first to set a param winning.
    /// </summary>
    private Dictionary<string, MaterialParam> Merge(string name, PipelineEntry entry, IReadOnlyDictionary<string, MaterialParam> inner)
    {
        Dictionary<string, MaterialParam> merged = new(StringComparer.Ordinal);
        Add(merged, settings.Params.GetValueOrDefault(name));
        Add(merged, entry.Params.GetValueOrDefault(name));
        Add(merged, inner);
        return merged;
    }

    private bool IsOff(string name)
    {
        foreach (string off in settings.Off)
        {
            if (string.Equals(off, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(Dictionary<string, MaterialParam> merged, IReadOnlyDictionary<string, MaterialParam>? layer)
    {
        if (layer is null)
        {
            return;
        }

        foreach ((string param, MaterialParam value) in layer)
        {
            merged.TryAdd(param, value);
        }
    }
}
