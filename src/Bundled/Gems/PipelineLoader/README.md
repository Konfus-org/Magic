# PipelineLoader

Pipelines as they load: the `ILoadPostProcessor` that gives each loaded `Pipeline` its `Passes`, the ordered list a
renderer runs, so no renderer walks a pipeline itself ([Core's README](../../../Core/README.md#the-gpu)).

## Settings

Each setting is documented on `PipelineSettings` and shown in the settings window. What a pipeline runs is the
project's or world's to say; the settings, which turn entries off and set params, are how a machine changes every
pipeline without touching either. Changed while running, every pipeline is made again, and runs the new way once
it is.

## A pipeline's passes

- Each entry, in order, is a pass, or a pipeline whose passes go in its place, as that pipeline loaded. An entry the
  settings turn off is left out with everything it brings in.
- A pass's params are merged, the first to set one winning: the settings', then each entry that brought it in, the
  outermost first, then the pass's own file. A param none of them sets is the shader's default, which only whoever
  compiles the shader knows.
- Each pass's merged params are a dictionary of its own, which the pipeline also holds by the pass's name, so a game
  sets a param of the pipeline being run, a world's effects say, while it runs
  ([Core's README](../../../Core/README.md#assets)). A value set so wins over the settings' until the pipeline is made
  again, which brings theirs back. A nested pipeline's passes are copied into each pipeline that brings it in, so
  setting the nested one's changes only where it is run itself. A name that runs twice in one pipeline is said, and
  its params by name are the first's.
- A pass or nested pipeline that does not load is an error, and the rest run; a pipeline that brings itself in fails
  that load where it does.
- It keeps nothing: the passes and pipelines it loads are what the assets make the pipeline from, so editing one makes
  every pipeline it is in again, each told on `Events` as a reload.
