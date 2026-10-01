using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Utils;

namespace Magic.Systems.Rendering;

/// <summary>
/// Asset hot reload for the render state, on the main thread before anything is recorded. Changed shaders drop everything
/// built on them: pipelines recompile and materials repack; materials and textures reload; passes follow their files and
/// shaders. When a material's class changed, every instance re-reads its class.
/// </summary>
internal static class AssetChanges
{
    /// <summary>Takes the frame's asset events: what changed reloads, and passes are listed again when files came, went or moved.</summary>
    public static void Apply(RenderContext ctx, ReadOnlySpan<Event> events)
    {
        HashSet<ulong>? changed = null;
        bool rediscover = false;
        foreach (Event e in events)
        {
            switch (e.Type)
            {
                case EventType.AssetAdded:
                    (changed ??= []).Add(e.Id);
                    rediscover = true;
                    break;
                case EventType.AssetModified:
                    (changed ??= []).Add(e.Id);
                    break;
                case EventType.AssetMoved or EventType.AssetRemoved:
                    rediscover = true;
                    break;
            }
        }

        if (rediscover)
            Passes.Discover(ctx);

        if (changed is null)
            return;

        HashSet<ulong> shaders = [];
        foreach (ulong id in changed)
        {
            if (Shaders.Owns(ctx, id))
                shaders.UnionWith(Shaders.Invalidate(ctx, id));
        }

        bool reclass = false;
        if (shaders.Count > 0)
        {
            Pipelines.Invalidate(ctx, shaders);
            reclass |= Materials.RepackShaders(ctx, shaders);
            Debugging.Log.Info($"Shaders changed: {shaders.Count} shader(s) rebuild.");
        }

        Passes.OnAssetsChanged(ctx, changed, shaders);
        bool textures = false;
        foreach (ulong id in changed)
        {
            if (ctx.Materials.Owns(id))
            {
                reclass |= Materials.Reload(ctx, id);
                Debugging.Log.Info($"Material {id} reloaded.");
            }

            if (ctx.Textures.Owns(id))
            {
                Textures.Reload(ctx, id);
                textures = true;
                Debugging.Log.Info($"Texture {id} uploaded again.");
            }

            if (ctx.Meshes.Owns(id))
                Debugging.Log.Warn($"Model {id} changed on disk; remove and re-add its entities to see the new geometry (live model reload arrives with streaming).");
        }

        // Records carry texture references resolved when they are written, and a texture that failed or was repaired
        // moves its materials to or from the failure surface.
        if (textures)
            reclass |= Materials.RepackTextures(ctx);

        if (reclass)
            Instancing.Reclass(ctx);
    }
}
