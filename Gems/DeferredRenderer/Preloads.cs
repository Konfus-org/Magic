using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Extensions;
using Magic.Services;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// Loads what the render tables are about to ask for, off the render thread, and hands it to them: a model with its
/// lesser versions, a material with its textures and its surface shader, a pass with its shader, a shader with the
/// files it includes. Whoever needs one asks whether it is <see cref="Ready"/>, which starts its load the first
/// time, and holds back until it is: an entity is not registered, so it is not drawn, a changed file is not applied,
/// a listed pass is not run. What arrived is a <see cref="Preloaded"/>, and while a table works with one
/// (<see cref="Use"/>) every asset it asks for (<see cref="Get{T}"/>) comes out of it. So the tables never load:
/// the one time they do is while the render state is being built, which has to finish before a frame can be drawn.
/// </summary>
internal static class Preloads
{
    /// <summary>
    /// Frames a load that nothing came for is kept: its entity went before it could be registered.
    /// </summary>
    private const long KeptFrames = 600;

    /// <summary>
    /// Whether everything the renderer draws with has arrived: its model and its materials. Starts the loads of what has not.
    /// </summary>
    public static bool Ready(RenderContext ctx, in Renderer renderer)
    {
        Handle<Model> model = renderer.Model;
        bool ready = !model.IsValid || ctx.Meshes.Contains(model.Id) || Arrived(ctx, model.Id, isModel: true);
        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            Handle<Material> material = renderer.Materials[i];
            ready &= !material.IsValid || ctx.Materials.Contains(material.Id) || Arrived(ctx, material.Id, isModel: false);
        }

        return ready;
    }

    /// <summary>
    /// What arrived for the renderer's model and materials is what the tables are handed until <see cref="Done"/>;
    /// they keep what they take, so it is let go of here.
    /// </summary>
    public static void Use(RenderContext ctx, in Renderer renderer)
    {
        if (ctx.Preloaded.Remove(renderer.Model.Id, out Preloaded? model))
            ctx.Using.Add(model);

        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            if (ctx.Preloaded.Remove(renderer.Materials[i].Id, out Preloaded? material))
                ctx.Using.Add(material);
        }
    }

    /// <summary>
    /// What arrived in <paramref name="preloaded"/> is what the tables are handed until <see cref="Done"/>.
    /// </summary>
    public static void Use(RenderContext ctx, Preloaded preloaded)
    {
        ctx.Using.Add(preloaded);
    }

    public static void Done(RenderContext ctx)
    {
        ctx.Using.Clear();
    }

    /// <summary>
    /// The asset, from what is in use. One that is not there was not loaded ahead: while the render state is built
    /// that is every one, and it is loaded here; after that it is a gap in what was loaded ahead, said once, and
    /// loaded here all the same so that it is drawn.
    /// </summary>
    public static T? Get<T>(RenderContext ctx, ulong id) where T : Asset
    {
        foreach (Preloaded preloaded in ctx.Using)
        {
            if (preloaded.Assets.TryGetValue(id, out Asset? asset))
                return asset as T;
        }

        if (!ctx.Building && ctx.Missed.Add(id))
            Debugging.Log.Warn($"{typeof(T).Name} {id} ({ctx.Assets.PathOf(id)}) was not loaded ahead of the render thread, which waited for it.");

        return ctx.Assets.Load(new Handle<T>(id));
    }

    /// <summary>
    /// The model's lesser versions, as <see cref="Get{T}"/> answers an asset.
    /// </summary>
    public static (float Threshold, Handle<Model> Asset)[] Lods(RenderContext ctx, Handle<Model> handle)
    {
        foreach (Preloaded preloaded in ctx.Using)
        {
            if (preloaded.Lods.TryGetValue(handle.Id, out (float Threshold, Handle<Model> Asset)[]? lods))
                return lods;
        }

        if (!ctx.Building && ctx.Missed.Add(handle.Id))
            Debugging.Log.Warn($"The LODs of model {handle.Id} ({ctx.Assets.PathOf(handle.Id)}) were not found ahead of the render thread, which waited for them.");

        return ctx.Assets.Lods(handle);
    }

    /// <summary>
    /// The finished loads are kept for whoever asked; one that threw is too, empty and logged, so the tables report
    /// its asset as they do any that fails. What nobody came for in a long while is let go of.
    /// </summary>
    public static void Poll(RenderContext ctx, long frame)
    {
        ctx.Preloads.Poll((ctx, frame), static (state, id, job) => state.ctx.Preloaded[id] = Outcome(state.ctx, id, job, state.frame));

        if (frame % KeptFrames != 0 || ctx.Preloaded.Count == 0)
            return;

        foreach ((ulong id, Preloaded preloaded) in ctx.Preloaded.ToArray())
        {
            if (frame - preloaded.Frame >= KeptFrames)
                ctx.Preloaded.Remove(id);
        }
    }

    /// <summary>
    /// What a finished load came to; empty, and logged, for one that threw.
    /// </summary>
    public static Preloaded Outcome(RenderContext ctx, ulong id, Task<Preloaded> job, long frame)
    {
        if (job.IsCompletedSuccessfully)
        {
            job.Result.Frame = frame;
            return job.Result;
        }

        Debugging.Log.Error($"Asset {id} ({ctx.Assets.PathOf(id)}) could not be loaded ahead of drawing: {job.Exception?.GetBaseException().Message}");
        return new Preloaded { Frame = frame, Assets = { [id] = null } };
    }

    /// <summary>
    /// The asset's file changed: what arrived is stale.
    /// </summary>
    public static void Forget(RenderContext ctx, ulong id)
    {
        ctx.Preloaded.Remove(id);
        ctx.Missed.Remove(id);
    }

    /// <summary>
    /// The model with its lesser versions, which are made first when they are not cached.
    /// </summary>
    public static async Task<Preloaded> ModelAsync(Assets assets, Handle<Model> handle, CancellationToken cancel)
    {
        Preloaded preloaded = new();
        Model? model = await assets.LoadAsync(handle, cancel: cancel).ConfigureAwait(false);
        preloaded.Assets[handle.Id] = model;
        if (model is null)
            return preloaded;

        (float Threshold, Handle<Model> Asset)[] lods = await assets.LodsAsync(handle, cancel: cancel).ConfigureAwait(false);
        preloaded.Lods[handle.Id] = lods;
        foreach ((_, Handle<Model> lesser) in lods)
            preloaded.Assets[lesser.Id] = await assets.LoadAsync(lesser, cancel: cancel).ConfigureAwait(false);

        return preloaded;
    }

    /// <summary>
    /// The material with its textures and its surface shader, includes too.
    /// </summary>
    public static async Task<Preloaded> MaterialAsync(Assets assets, Handle<Material> handle, CancellationToken cancel)
    {
        Preloaded preloaded = new();
        Material? material = await assets.LoadAsync(handle, cancel: cancel).ConfigureAwait(false);
        preloaded.Assets[handle.Id] = material;
        if (material is null)
            return preloaded;

        await ShaderAsync(assets, material.Shader.Id, preloaded, cancel).ConfigureAwait(false);
        foreach (object texture in material.ValuesOf<Handle<Texture>>(strict: true))
            await TextureAsync(assets, ((Handle<Texture>)texture).Id, preloaded, cancel).ConfigureAwait(false);

        return preloaded;
    }

    /// <summary>
    /// The image of a texture, or the description of a render texture.
    /// </summary>
    public static async Task<Preloaded> TextureAsync(Assets assets, ulong id, CancellationToken cancel)
    {
        Preloaded preloaded = new();
        await TextureAsync(assets, id, preloaded, cancel).ConfigureAwait(false);

        return preloaded;
    }

    /// <summary>
    /// The pass with its shader, includes too.
    /// </summary>
    public static async Task<Preloaded> PassAsync(Assets assets, ulong id, CancellationToken cancel)
    {
        Preloaded preloaded = new();
        Pass? pass = await assets.LoadAsync(new Handle<Pass>(id), cancel: cancel).ConfigureAwait(false);
        preloaded.Assets[id] = pass;
        if (pass is not null)
            await ShaderAsync(assets, pass.Shader.Id, preloaded, cancel).ConfigureAwait(false);

        return preloaded;
    }

    /// <summary>
    /// The text of every one of the shaders, as they are now, and of every file they include.
    /// </summary>
    public static async Task<Preloaded> ShadersAsync(Assets assets, ulong[] shaders, CancellationToken cancel)
    {
        Preloaded preloaded = new();
        foreach (ulong id in shaders)
            await ShaderAsync(assets, id, preloaded, cancel).ConfigureAwait(false);

        return preloaded;
    }

    /// <summary>
    /// Whether the model or material has arrived, starting its load when nothing is loading it. One that is pooled
    /// already arrives here and now.
    /// </summary>
    private static bool Arrived(RenderContext ctx, ulong id, bool isModel)
    {
        if (ctx.Preloaded.ContainsKey(id))
            return true;
        if (ctx.Preloads.IsRunning(id))
            return false;

        ctx.Preloads.Start(id, cancel => isModel
            ? ModelAsync(ctx.Assets, new Handle<Model>(id), cancel)
            : MaterialAsync(ctx.Assets, new Handle<Material>(id), cancel));
        if (!ctx.Preloads.TryTake(id, out Preloaded? loaded))
            return false;

        ctx.Preloaded[id] = loaded;
        return true;
    }

    private static async Task TextureAsync(Assets assets, ulong id, Preloaded into, CancellationToken cancel)
    {
        if (id == 0 || into.Assets.ContainsKey(id))
            return;

        into.Assets[id] = RenderTexture.IsAt(assets.PathOf(id))
            ? await assets.LoadAsync(new Handle<RenderTexture>(id), cancel: cancel).ConfigureAwait(false)
            : await assets.LoadAsync(new Handle<Texture>(id), cancel: cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The shader's text and that of every file it includes, by the paths its <c>#include</c>s write.
    /// </summary>
    private static async Task ShaderAsync(Assets assets, ulong id, Preloaded into, CancellationToken cancel)
    {
        if (id == 0 || into.Assets.ContainsKey(id))
            return;

        Shader? shader = await assets.LoadAsync(new Handle<Shader>(id), cancel: cancel).ConfigureAwait(false);
        into.Assets[id] = shader;
        if (shader is null)
            return;

        foreach (string include in shader.Includes)
            await ShaderAsync(assets, assets.Find<Shader>("Shaders/" + include).Id, into, cancel).ConfigureAwait(false);
    }
}
