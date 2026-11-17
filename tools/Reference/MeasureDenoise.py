# Measures how the diffuse light's denoiser settles after motion and how noisy it is, at a view of
# tools/Scripts/ReferenceViews.json, in Magic's light before the exposure (HDR screenshots). Run it with Python from
# the repository root:
#   python tools/Reference/MeasureDenoise.py --view cabin-wall --out <folder> [--hour 18 [--to 8]] [--runs 3]
#     [--cycles cycles.npy] [--set 'Rendering.ProbeUpdateSpeed 0.5' ...] [--exposure 0.01]
# It builds Magic first, so no run reads old DLLs, prints the commit, and before each run waits until no other Magic
# runs, as other sessions share the GPU. Each run is one run of Magic, in this order:
#   still       the camera still at the view: the still light, the mean of 6 frames
#   turn <d>    the player's Spin script turns the camera at d degrees a second, in the frame time Magic runs at, as a
#               mouse does, for 24 frames, each saved; then the camera stops and 30 frames are saved
#   strafe <m>  the camera moves sideways m metres a frame for 30 frames, the last 6 saved (each of those a frame
#               without motion after a double step); then it stops and 30 frames are saved
#   light       with --to, the sun set at once from --hour to --to, then 40 frames, then the new still light
# 8 s after each motion, 6 frames at the pose where it stopped are its still light.
# It prints, for each motion, the median of the runs and each run's value: the frames until 98 % of 16 pixel patches
# stay within 5 % of the still light (or 3 times their spread over the still frames), and on held frames 1, 5, 10
# and 30 the error of the strip of the picture that the turn uncovered and the 95th percentile of the patches' error
# over all the picture, which sees light that the motion uncovered inside the picture. Error is |light - still| over
# the still. It writes each frame of the first run as a PNG at --exposure into the out folder, named in time order, so
# that the PNGs of two runs compare.
import argparse
import json
import math
import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LUMINANCE = np.array([0.2126, 0.7152, 0.0722])
# The sun's rotation at an hour at the cabin, which BuildScene.py --sun prints. Other hours go through the day cycle.
SUN = {
    12: (0.061629, 0.612373, -0.704416, -0.353553),
    8: (0.230149, 0.882538, -0.303856, -0.275372),
}
TURNS = (90, 180, 360)
STRAFES = (0.05,)
TURN_FRAMES = 24
HELD = 30
# What the PNGs multiply the light by: about 0.18 over the cabin wall's median light at hour 18.
EXPOSURE = 0.01


def read_pfm(path):
    with open(path, 'rb') as f:
        kind = f.readline().strip()
        width, height = (int(v) for v in f.readline().split())
        scale = float(f.readline())
        data = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4')
    return np.flipud(data.reshape(height, width, 3 if kind == b'PF' else 1)).astype(np.float32)


def rotation(yaw, pitch):
    y, p = math.radians(yaw) / 2, math.radians(pitch) / 2
    q = (math.cos(y) * math.sin(p), math.sin(y) * math.cos(p), -math.sin(y) * math.sin(p), math.cos(y) * math.cos(p))
    return "set player.transform.rotation '{\"x\":%f,\"y\":%f,\"z\":%f,\"w\":%f}'" % q


def position(x, y, z):
    return "set player.transform.position '{\"x\":%f,\"y\":%f,\"z\":%f}'" % (x, y, z)


def spin(degrees):
    return 'set player.scriptcontainer \'[{"id":100411,"degreesPerSec":%g},{"id":100412}]\'' % degrees


def hour(h):
    if h in SUN:
        return ["set sun.scriptcontainer '[]'", "set sun.transform.rotation '{\"x\":%f,\"y\":%f,\"z\":%f,\"w\":%f}'" % SUN[h]]
    return ["set sun.scriptcontainer '[{\"id\":100421,\"hour\":%g,\"hoursPerMinute\":0}]'" % h]


def commands(view, args):
    p = view['position']
    pose = [position(p['x'], p['y'], p['z']), rotation(view['yaw'], view['pitch'])]
    # The window pinned at 1600 x 900, so that runs on any display measure the same picture.
    c = ['set Window.Mode Windowed', "set Window.WindowedSize '{\"width\":1600,\"height\":900}'",
         'wait 20s', spin(0), 'set Rendering.FieldOfView 60'] + ['set ' + line for line in args.set] + ['wait 30']
    if args.hour is not None:
        c += hour(args.hour)
    c += pose + ['wait 15s']
    labels = []

    def stills(name):
        for _ in range(6):
            c.extend(['screenshot --hdr', 'wait 1']); labels.append(name)

    def held(name):
        for _ in range(HELD):
            c.append('screenshot --hdr'); labels.append(f'{name}-held')
        c.append('wait 8s')
        stills(f'{name}-still')

    stills('still')
    for speed in TURNS:
        # Starts half the turn's way back at an estimated 100 frames a second, so that it ends near the view.
        c.extend(pose[:1] + [rotation(view['yaw'] - speed * TURN_FRAMES / 200.0, view['pitch']), 'wait 3s', spin(speed)])
        for _ in range(TURN_FRAMES):
            c.append('screenshot --hdr'); labels.append(f'turn{speed}-moving')
        c.append(spin(0))
        held(f'turn{speed}')
    yaw = math.radians(view['yaw'])
    right = (math.cos(yaw), 0, -math.sin(yaw))
    for step in STRAFES:
        frames = 30
        start = (p['x'] - right[0] * step * frames / 2, p['y'], p['z'] - right[2] * step * frames / 2)
        c.extend([position(*start), pose[1], 'wait 3s'])
        f = 0
        while f < frames:
            # The last frames move two steps, then hold one frame for the screenshot line, so the speed stays.
            f += 2 if f >= frames - 12 else 1
            c.append(position(start[0] + right[0] * step * f, start[1], start[2] + right[2] * step * f))
            if f > frames - 12:
                c.append('screenshot --hdr'); labels.append(f'strafe{step:g}-moving')
        held(f'strafe{step:g}')
    if args.to is not None:
        c.extend(pose + ['wait 15s'] + hour(args.to))
        for _ in range(40):
            c.append('screenshot --hdr'); labels.append('light')
        c.append('wait 15s')
        stills('light-still')
    return c, labels


def patches(lum, size=16):
    h, w = lum.shape[0] // size * size, lum.shape[1] // size * size
    return lum[:h, :w].reshape(h // size, size, w // size, size).mean(axis=(1, 3))


def errors(frame, still):
    """Each lit 16 pixel patch's |light - still| over the still."""
    s = patches(still)
    lit = s > np.percentile(s, 10)
    return np.abs(patches(frame) - s)[lit] / s[lit]


def settle(frames, still, spread):
    """The frames until 98 % of the lit patches stay within 5 % of the still light, or within 3 times the patch's own
    spread over the still frames where it is noisier than that."""
    ref = patches(still)
    lit = ref > np.percentile(ref, 10)
    limit = np.maximum(0.05, 3 * spread)[lit]
    share = [(np.abs(patches(f)[lit] / ref[lit] - 1) < limit).mean() for f in frames]
    for i in range(len(frames)):
        if all(s >= 0.98 for s in share[i:]):
            return i
    return len(frames)


def spread(frames):
    return np.std([patches(f) for f in frames], axis=0) / patches(np.mean(frames, axis=0))


def uncovered(frame, still):
    """The mean error of the strip, 15 % of the width, at the side the turn brought into view: the larger side's."""
    cut = int(frame.shape[1] * 0.15)
    return max(float(np.mean(errors(frame[:, :cut], still[:, :cut]))),
               float(np.mean(errors(frame[:, -cut:], still[:, -cut:]))))


def wait_for_gpu():
    """Waits until no other Magic runs, checking each 20 s for up to 10 minutes."""
    for _ in range(30):
        if 'magic.exe' not in subprocess.run(['tasklist'], capture_output=True, text=True).stdout.lower():
            return
        time.sleep(20)
    print('Another Magic still runs after 10 minutes; this run shares the GPU.', flush=True)


def run_once(view, args, out, save_png):
    c, labels = commands(view, args)
    os.makedirs(out, exist_ok=True)
    quoted = ', '.join("'" + x.replace("'", "''") + "'" for x in c)
    script = os.path.join(out, 'run.ps1')
    with open(script, 'w') as f:
        f.write(f"& '{ROOT}/tools/Scripts/Screenshot.ps1' -Configuration {args.configuration} -Settle 10 -Seconds 400 "
                f"-Project '{view['project']}' -Commands @({quoted})\nexit $LASTEXITCODE\n")
    wait_for_gpu()
    run = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script],
                         capture_output=True, text=True, timeout=900)
    open(os.path.join(out, 'run.log'), 'w', encoding='utf-8').write(run.stdout + run.stderr)
    paths = sorted((l.strip() for l in run.stdout.splitlines() if l.strip().endswith('.pfm')), key=os.path.basename)
    if len(paths) != len(labels):
        sys.exit(f'exit {run.returncode}: {len(paths)} HDR frames, not {len(labels)}; see {out}/run.log')

    frames = {}
    for i, (path, label) in enumerate(zip(paths, labels)):
        rgb = read_pfm(path)
        os.remove(path)
        frames.setdefault(label, []).append(rgb @ LUMINANCE)
        if save_png:
            mapped = rgb * args.exposure
            mapped = mapped / (1 + (mapped @ LUMINANCE)[..., None])
            png = np.clip(mapped, 0, 1) ** (1 / 2.2) * 255
            Image.fromarray(png.astype(np.uint8)).save(os.path.join(out, f'{i:03d}-{label}.png'))

    rows = {'still: median light': float(np.median(frames['still'][0])),
            'still: spread of a patch over 6 frames': float(np.median(spread(frames['still'])))}
    # The time between the turns' frames, from the screenshots' names, which end in HHmmss-fffffff.
    names = [os.path.splitext(os.path.basename(p))[0][-14:] for p, l in zip(paths, labels) if l.endswith('moving')]
    seconds = [int(s[0:2]) * 3600 + int(s[2:4]) * 60 + int(s[4:6]) + int(s[7:14]) / 1e7 for s in names]
    gaps = [b - a for a, b in zip(seconds, seconds[1:]) if 0 < b - a < 0.5]
    if gaps:
        rows['frame time while moving, ms'] = 1000 * float(np.median(gaps))
    for name in [f'turn{s}' for s in TURNS] + [f'strafe{s:g}' for s in STRAFES]:
        still = np.mean(frames[f'{name}-still'], axis=0)
        held = frames[f'{name}-held']
        rows[f'{name}: frames to settle'] = settle(held, still, spread(frames[f'{name}-still']))
        for k in (1, 5, 10, 30):
            if name.startswith('turn'):
                rows[f'{name}: uncovered strip error, held {k}'] = uncovered(held[k - 1], still)
            rows[f'{name}: p95 patch error, held {k}'] = float(np.percentile(errors(held[k - 1], still), 95))
        rows[f'{name}: p95 patch error, still frames'] = float(np.mean(
            [np.percentile(errors(f, still), 95) for f in frames[f'{name}-still']]))
    if 'light' in frames:
        after = np.mean(frames['light-still'], axis=0)
        rows['light change: frames to settle'] = settle(frames['light'], after, spread(frames['light-still']))
    if args.cycles:
        cycles = np.load(args.cycles) @ LUMINANCE * 683
        still = np.mean(frames['still'], axis=0)
        if cycles.shape == still.shape:
            rows['still median over Cycles'] = float(np.median(still / np.maximum(cycles, 1e-6)))
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--view', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--cycles')
    parser.add_argument('--hour', type=float, help='the hour of the sun, 0 to 24')
    parser.add_argument('--to', type=float, help='the hour the sun is set to after the motions')
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--configuration', default='Debug')
    parser.add_argument('--set', action='append', default=[], help='a setting and its value, as the console sets it')
    parser.add_argument('--exposure', type=float, default=EXPOSURE, help='what the PNGs multiply the light by')
    args = parser.parse_args()
    build = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                            os.path.join(ROOT, 'tools/Scripts/Validate.ps1'), '-Only', 'Build',
                            '-Configuration', args.configuration], capture_output=True, text=True, timeout=1200)
    if build.returncode != 0:
        sys.exit(build.stdout[-3000:] + build.stderr[-3000:])
    git = ['git', '-C', ROOT]
    commit = subprocess.run(git + ['rev-parse', '--short', 'HEAD'], capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(git + ['status', '--porcelain', '--untracked-files=no'], capture_output=True, text=True).stdout
    print(f'commit {commit}{" dirty" if dirty.strip() else ""}, {args.configuration}, exposure {args.exposure:g}',
          flush=True)
    args.hour, args.to = (int(h) if h is not None and h == int(h) else h for h in (args.hour, args.to))
    view = next(v for v in json.load(open(os.path.join(ROOT, 'tools/Scripts/ReferenceViews.json'))) if v['name'] == args.view)
    results = [run_once(view, args, args.out if i == 0 else os.path.join(args.out, f'run{i + 1}'), i == 0)
               for i in range(args.runs)]
    for name in results[0]:
        values = [r[name] for r in results]
        each = ' '.join(f'{v:.3f}' if isinstance(v, float) else str(v) for v in values)
        print(f'{name:50s} {float(np.median(values)):8.3f}   ({each})')


if __name__ == '__main__':
    main()
