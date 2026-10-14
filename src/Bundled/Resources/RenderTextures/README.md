# Render textures

A `.rtex` file is a texture a camera draws into, which materials sample like any other (`RenderTexture`):

```json
{ "width": 1024, "height": 512 }
```

| Field | Default | What it is |
| --- | --- | --- |
| `width` | 512 | Texels across: what the camera draws at. |
| `height` | 512 | Texels down. |

It has no image: it is black until a camera has drawn into it. A camera names one as what it draws into
(`"target": { "texture": { "id": 740 } }`), and a material samples one as a param (`{ "renderTexture": { "id": 740 } }`).
