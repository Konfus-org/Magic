# PipelineLoader

Turns each loaded `Pipeline` into the ordered list of passes a renderer runs. Provides `ILoadPostProcessor<Pipeline>`
([The GPU](../../../Core/README.md#the-gpu)).

A pipeline lists passes and other pipelines. A nested pipeline's passes take its place in the list. A pass's params
come from this gem's settings, then the pipelines that include it (outermost first), then the pass's own file. The
first to set a param wins. A game can change a running pipeline's params by pass name
([Assets](../../../Core/README.md#assets)).

Its settings are on `PipelineSettings` and show in the settings window (F4). They let a machine leave a pass out of
every pipeline, or override a pass's params. Editing a pass or pipeline rebuilds every pipeline that uses it.

- A pass that fails to load is an error. The rest of the pipeline still runs.
- A pipeline can't include itself.
