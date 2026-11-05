# MeshPreprocessor

Makes what the renderer draws a model as, and keeps it in the cache under the model's version: provides
`IMeshPreprocessor`.

To use it, list `MeshPreprocessor` in a project's `.magic` file after the gem that reads models.

- A model becomes its vertices as the GPU reads them, and a hierarchy of clusters of up to 128 triangles: leaves cut
  from the mesh, and above them each level grouping neighbouring clusters, simplifying the group with its border
  locked and clustering the result, up to one group or until nothing simplifies further; the root above is the mesh
  simplified without regard to its topology down to a dozen triangles.
- Every cluster carries its own error and its parent group's, so which clusters to draw is decided on the GPU, per
  cluster, with no walk. A root's parent error is the model's diameter, so a model draws its root until the whole of
  it is under a pixel, and then nothing.
- A model also gets a signed distance field: a cube of voxels around its bounds, each the distance to the nearest
  triangle, negative inside, clamped to a few voxels either side of a surface.
- meshoptimizer does the clustering, grouping and simplifying; the kit builds it from source.
