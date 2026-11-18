# The measures that judge a comparison, in place of an eye: the ratio over patches, the fireflies, the banding, the
# flicker of a still picture and NVIDIA's FLIP. It also reads the HDR frames that Magic writes, which every tool here
# measures from. tools/Reference/Compare.py calls the measures and writes each one as a row of its JSON. Each one
# takes plain arrays of light, so made-up data tests it. To run those tests, run this file with Blender's Python:
#   "<Blender>/python/bin/python.exe" tools/Reference/Metrics.py
import numpy as np

# How bright a picture of light is, as the eye sees it, in the Rec.709 primaries.
LUMINANCE = np.array([0.2126, 0.7152, 0.0722])
# Cycles writes radiance in W/sr/m2. Magic writes nits, which is this many times as much.
NITS_PER_WATT = 683
# The patch that a ratio is measured over. 8 by 8 pixels hold enough of the reference's noise for it to cancel, and
# are still smaller than a surface of the scene.
PATCH = 8
# The window a firefly is measured against, how far over its window's median a firefly is, and how far over the
# picture's own median, which keeps the dark pixels of a corner out.
FIREFLY_WINDOW = 5
FIREFLY_FACTOR = 8
# The part of the still frames that a pixel holds a dot in before it counts.
FIREFLY_PART = 0.5
FIREFLY_FLOOR = 2
# The pixels across a row that banding is measured over.
BANDING_RUN = 32


def read_pfm(path):
    """The light of one HDR frame that Magic wrote, top row first, in nits."""
    with open(path, 'rb') as f:
        kind = f.readline().strip()
        width, height = (int(v) for v in f.readline().split())
        scale = float(f.readline())
        data = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4')
    channels = 3 if kind == b'PF' else 1
    return np.flipud(data.reshape(height, width, channels)).astype(np.float64)


def luminance(rgb):
    """How bright each pixel of a colour picture is."""
    return rgb @ LUMINANCE


def patch_ratios(magic_light, reference_light, lit, size=PATCH):
    """The ratio of Magic's light to the reference's over patches of size by size pixels, as one value for each patch
    that the reference lights. The ratio is the sum of a patch's lit pixels in Magic over the same sum in the
    reference, so the reference's own noise, which has no mean, cancels inside the patch."""
    height, width = reference_light.shape
    rows, columns = height // size, width // size

    def block_sums(picture):
        return picture[:rows * size, :columns * size].reshape(rows, size, columns, size).sum(axis=(1, 3))

    counts = block_sums(lit.astype(np.float64))
    full = counts >= size * size / 2
    return block_sums(magic_light * lit)[full] / np.maximum(block_sums(reference_light * lit)[full], 1e-9)


def window_medians(picture, window):
    """The median of the window by window pixels about each pixel. A pixel at an edge takes the nearest full window.
    The rows are taken in blocks, so the windows of a whole picture never stand in memory at one time."""
    half = window // 2
    padded = np.pad(picture.astype(np.float32), half, mode='edge')
    windows = np.lib.stride_tricks.sliding_window_view(padded, (window, window))
    middle = window * window // 2
    out = np.empty(picture.shape, dtype=np.float32)
    for start in range(0, picture.shape[0], 64):
        block = windows[start:start + 64].reshape(-1, window * window)
        out[start:start + 64] = np.partition(block, middle, axis=-1)[:, middle].reshape(-1, picture.shape[1])
    return out


def hot_pixels(picture, window=FIREFLY_WINDOW, factor=FIREFLY_FACTOR, floor=FIREFLY_FLOOR):
    """Where a picture is far brighter than the pixels about it: over factor times the median of its window, and over
    floor times the picture's own median."""
    return (picture > factor * window_medians(picture, window)) & (picture > floor * np.median(picture))


def fireflies(magic_frames, reference_light, part=FIREFLY_PART):
    """How many pixels in a million hold a white dot that a path tracer leaves and a denoiser keeps: far brighter
    than the pixels about them in more than part of Magic's still frames, where the reference has no such pixel.
    One noisy frame is not enough, because a dot that comes and goes is the noise of a frame and not a fault of the
    picture; a dot the renderer leaves in the same place stands in most of them."""
    hot = sum(hot_pixels(frame).astype(np.int32) for frame in magic_frames)
    return 1e6 * float(((hot > part * len(magic_frames)) & ~hot_pixels(reference_light)).sum()) / reference_light.size


def display_levels(light, exposure):
    """Light as the steps of an 8-bit screen: the exposure, then the screen's gamma, then 255 steps. One step is 1."""
    return 255 * np.clip(light * exposure, 0, 1) ** (1 / 2.2)


def banding(magic_levels, reference_levels, lit, run=BANDING_RUN, step=1.0, parts=4):
    """How much of a smooth gradient Magic draws in steps, from 0 to 1. It takes both pictures in the steps of a
    screen, where 1 is one step. First it finds the gradients: each run of run pixels across a row where the
    reference is lit, and where the means of the run's parts go one way, each by half a step or more, and the run
    rises or falls by run steps or fewer. The means hold the reference's own noise out of that choice, the way the
    patch ratios do. Then, in those runs, it adds up how much Magic changes between neighbouring pixels, and gives
    the part of that change which comes in a jump of a step or more that has a flat pixel on each side of it, which
    is what the edge of a band is. Magic draws the gradient as smoothly as the reference: about 0. Magic draws it as
    flat bands with a jump between them: about 1. Noise, which moves every pixel and leaves none flat, gives 0."""
    height, width = magic_levels.shape
    columns = width // run

    def in_runs(picture):
        return picture[:, :columns * run].reshape(height * columns, run)

    steps = np.diff(in_runs(reference_levels).reshape(-1, parts, run // parts).mean(axis=2), axis=1)
    one_way = np.all(steps >= 0.5 * step, axis=1) | np.all(steps <= -0.5 * step, axis=1)
    keep = in_runs(lit).all(axis=1) & one_way & (np.abs(steps.sum(axis=1)) <= run * step)
    if not keep.any():
        return 0.0
    changes = np.abs(np.diff(in_runs(magic_levels)[keep], axis=1))
    flat = changes < step / 4
    edges = np.zeros_like(flat)
    edges[:, 1:-1] = (changes[:, 1:-1] >= step) & flat[:, :-2] & flat[:, 2:]
    total = float(changes.sum())
    return float(changes[edges].sum() / total) if total > 0 else 0.0


def flicker(frames, lit):
    """How much a still picture of a still scene moves from frame to frame: the mean over the lit pixels of the
    standard deviation over the frames divided by their mean. A picture that does not move gives 0, and so does a
    picture with no lit pixel, such as the sealed room."""
    if not lit.any():
        return 0.0
    stack = np.stack(frames)
    return float((stack.std(axis=0) / np.maximum(stack.mean(axis=0), 1e-9))[lit].mean())


def flip(magic, reference, exposure):
    """NVIDIA's FLIP of Magic's colour against the reference's, both at the comparison's exposure: the mean error a
    person sees, from 0 to 1, and the map of it in the magma colours. FLIP's HDR mode takes its own exposure steps
    about the one it is given. It needs the flip-evaluator package (tools/Reference/requirements.txt)."""
    import flip_evaluator

    def ready(picture):
        return np.ascontiguousarray(np.clip(picture, 0, None) * exposure, dtype=np.float32)

    error, mean, _ = flip_evaluator.evaluate(ready(reference), ready(magic), 'HDR')
    return float(mean), np.asarray(error, dtype=np.float64)


def _tests():
    """Each measure on made-up data, with the answer that the data gives it."""
    random = np.random.default_rng(7)
    shape = (256, 256)
    lit = np.ones(shape, dtype=bool)

    # A reference with noise of no mean, and a Magic that holds the same light without noise. The per-pixel ratios
    # spread with the noise; the patch ratios do not.
    truth = np.full(shape, 10.0)
    noisy = truth * (1 + 0.4 * random.standard_normal(shape))
    per_pixel = truth / np.maximum(noisy, 1e-9)
    patches = patch_ratios(truth, noisy, lit)
    print(f'patch ratios     per pixel {np.percentile(per_pixel, 10):.3f} to {np.percentile(per_pixel, 90):.3f}, '
          f'over patches {np.percentile(patches, 10):.3f} to {np.percentile(patches, 90):.3f} '
          f'(want about 1 plus or minus {1.2816 * 0.4 / PATCH:.3f}, the noise over {PATCH * PATCH} pixels)')

    # Forty hot pixels in Magic that the reference does not have, in every frame, and forty more in one frame alone,
    # which is the noise of a frame and not a fault of the picture.
    flat = np.full(shape, 1.0)
    hot = flat.copy()
    places = random.choice(flat.size, 80, replace=False)
    hot.ravel()[places[:40]] = 50
    once = hot.copy()
    once.ravel()[places[40:]] = 50
    frames = [once, hot, hot, hot]
    print(f'fireflies        {fireflies(frames, flat):.1f} and {fireflies([flat] * 4, hot):.1f} in a million '
          f'(want {1e6 * 40 / flat.size:.1f} and 0.0)')

    # A gradient across the picture, drawn smoothly and drawn in whole steps.
    smooth = np.tile(np.linspace(20, 80, shape[1]), (shape[0], 1))
    stepped = np.round(smooth / 4) * 4
    print(f'banding          smooth {banding(smooth, smooth, lit):.3f}, in steps {banding(stepped, smooth, lit):.3f} '
          f'(want 0.000 and 1.000)')

    # Six frames of one still view: the same picture, then the same picture that moves by a tenth.
    still = [truth.copy() for _ in range(6)]
    moving = [truth * (1 + 0.1 * value) for value in (-1, -0.6, -0.2, 0.2, 0.6, 1)]
    print(f'flicker          still {flicker(still, lit):.4f}, moving {flicker(moving, lit):.4f} '
          f'(want 0.0000 and {np.std([-0.1, -0.06, -0.02, 0.02, 0.06, 0.1]):.4f})')

    # FLIP of a picture against itself, and against one that is a fifth brighter.
    colour = np.clip(random.random((64, 64, 3)), 0.02, None)
    same, _ = flip(colour, colour, 1.0)
    brighter, error = flip(colour * 1.2, colour, 1.0)
    print(f'mean FLIP        the same picture {same:.4f}, a fifth brighter {brighter:.4f} '
          f'(want 0.0000 and more), map {error.shape}')


if __name__ == '__main__':
    _tests()
