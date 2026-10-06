# Pipelines

How a frame is rendered, as data: a `.pipeline` file lists the [passes](../Passes/README.md) of each stage in the
order they run. The renderer runs the stages in a fixed order and, within a stage, the passes in the order written.

```json
{
    "shadows":      [ { "id": 10100 }, { "id": 10106 }, … ],
    "gi":           [ { "id": 10180 }, … ],
    "scene":        [ { "id": 10120 }, … ],
    "lighting":     [ { "id": 10130 }, … ],
    "sky":          [ { "id": 10140 } ],
    "transparency": [ { "id": 10152 }, … ]
}
```

Every stage is optional (default `[]`). There is no post stage here: the world's `PostProcessing` component lists the
posts, since what a scene looks like at the end is the scene's business.

## Stages

| Stage | Runs | Default passes |
| --- | --- | --- |
| `shadows` | once a frame | ShadowPlan, ShadowCandidates, LocalShadowSelect, SeedShadowArgs, CullShadow, ClearTiles, ShadowDraw, ShadowImpostors: the sun's cascades and local light pages planned, culled and drawn into one depth atlas |
| `gi` | once a frame | GiPlan, BrickClear, BrickBuild, CollectPages, CollectInstances, Stamp, Resolve, DistanceX/Y/Z, LightGrid, GiSky, Propagate: one level of the voxel GI rebuilt |
| `scene` | per view | PageCull, CullEarly, DrawEarly, Glows, HiZBuild, SeedLate, CullLate, DrawLate: two-phase GPU culling and the gbuffer draw |
| `lighting` | per view | LightCull, LightCluster, Gtao, AoBlur, Lighting: lights binned, ambient occlusion, the gbuffer shaded into `Hdr` |
| `sky` | per target | Sky: paints what nothing was drawn over |
| `transparency` | per view | TransparentPrepare, TransparentLightCull, TransparentEarly/Late, TransparentComposite, then the Glass* passes: weighted blended transparency and refraction |
| post | per target | the world's `PostProcessing` list (Tonemap, Vignette, …) |

After the stages the renderer copies `Ldr` to the window or render texture (if no post wrote `Ldr`, `Hdr` is shown
as it is) and draws the failure bands of disabled passes.

## Which pipeline renders

1. `--pipeline Pipelines/Mine.pipeline` on the command line;
2. else the project's `"pipeline": { "id": N }` in its `.magic` file;
3. else `Pipelines/Default.pipeline` (id 10040).

A path names a file under Resources or the project's Assets. A pipeline that cannot be read leaves only the window's
clear colour, and the log says why.

## Lifetime

The pipeline is loaded at start-up and reloaded live when its file changes. On every change the renderer loads the
passes it now lists, unloads (with what they created) the ones it no longer lists, and fits the listing again: every
name a pass reads must be made by an engine resource or a pass before it. A pass listed in two stages runs in the
first.

## What must agree

- Each id is a `.pass` asset (a `.post` is listed by `PostProcessing`, never here).
- A pass comes after every pass that creates what it reads, in a stage whose scope can see it (frame-scope
  resources from `shadows` and `gi` are visible to all; a view's resources only within that view). See "What must
  agree" in the [Passes README](../Passes/README.md#what-must-agree).
- Draw passes only in `shadows`, `scene` and `transparency`; quads only in `scene` and `shadows`.

Taking a pass out disables every later pass that reads what it made. `Lighting.pass` reads the GI volumes
(`GiShR`, `GiSkyVis`, …), so a pipeline without the `gi` stage needs a lighting pass that does not read them; the
`Lighting.pass` params `giIntensity` and friends only tune GI, they do not remove it. The listing check logs every
pass that cannot fit and why.

## Adding a pipeline

For a project: copy `Default.pipeline` into the project's `Assets/` (say `Assets/Pipelines/Mine.pipeline`), give
its `.meta` a new id (or delete the copied `.meta` and let one be minted), change the lists, and name it in the
`.magic` file: `"pipeline": { "id": N }`. Project passes and engine passes mix freely; list them by id.

To try one without changing the project: `--pipeline Pipelines/Mine.pipeline`.
