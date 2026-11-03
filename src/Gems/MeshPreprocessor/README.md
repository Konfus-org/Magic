# MeshPreprocessor

Makes what the renderer draws a model as, and keeps it in the cache under the model's version: provides
`IMeshPreprocessor`.

To use it, list `MeshPreprocessor` in a project's `.magic` file after the gem that reads models.

- A model becomes its vertices as the GPU reads them, and a hierarchy of clusters of up to 128 triangles: leaves cut
  from the mesh, and above them each level grouping neighbouring clusters, simplifying the group with its border
  locked and clustering the result, up to one group or until nothing simplifies further.
- Every cluster carries its own error and its parent group's, so which clusters to draw is decided on the GPU, per
  cluster, with no walk. Above the roots sits the imposter cluster, a quad the renderer draws from the model's
  captures when the whole model is small on screen.
- meshoptimizer does the clustering, grouping and simplifying; the kit builds it from source.
