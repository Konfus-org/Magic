# PipelineLoader

Pipelines as they load: the `ILoadPostProcessor` that gives each loaded `Pipeline` its `Passes`. A renderer loads a
pipeline as any asset, `assets.LoadAsync`, and runs its passes; which pipeline its frame runs is one of the renderer's
settings.

## Settings

In the project's `Presets/Default.preset`, by another preset (`--preset Toaster`), or with
`--set PipelineLoader.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `off` | `[]` | Passes and pipelines this machine leaves out wherever they are listed, by their file name: `["AutoExposure"]`. |
| `params` | `{}` | Pass params that win over every pipeline's and the pass's own, by the pass's file name, then the param's: `{ "Tonemap": { "exposureBias": { "x": -0.5 } } }`. |

```json
{
    "off": [ "AutoExposure" ]
}
```

What a pipeline runs is the project's or world's to say; `off` and `params` are how a machine changes every pipeline
without touching either. Changed while running, every pipeline is made again, and runs the new way once it is.

## A pipeline's passes

- Each entry, in order, is a pass, or a pipeline whose passes go in its place, as that pipeline loaded. An entry the
  settings turn off is left out with everything it brings in.
- A pass's params are merged, the first to set one winning: the settings', then each entry that brought it in, the
  outermost first, then the pass's own file. A param none of them sets is the shader's default, which only whoever
  compiles the shader knows.
- A pass or nested pipeline that does not load is an error, and the rest run; a pipeline that brings itself in fails
  that load where it does.
- It keeps nothing: the passes and pipelines it loads are what the assets make the pipeline from, so editing one makes
  every pipeline it is in again, each an `AssetReloaded<Pipeline>`.
