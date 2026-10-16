# LogMetrics

Logs what the engine measures ([Core's README](../../../Core/README.md#telemetry)): every 30 seconds, a line per
instrument and set of tags measured since the last time, then the slowest frame and where its time went, and once more
when the run ends. It provides nothing, and does nothing until enabled:

```
Magic --project samples --set LogMetrics.enabled=true
```

```
12:00:30.001 Info     f1800 t2 magic.core.frame.duration: 1800 in 30 s, mean 16.67 ms, min 15.9 ms, max 87.2 ms (LogMetricsGem.cs:267 Report)
12:00:30.001 Info     f1800 t2 magic.core.gem.hook.duration {magic.core.gem.name=Window, magic.core.frame.phase=Update}: 1800 in 30 s, mean 0.21 ms, min 0.05 ms, max 71.3 ms (LogMetricsGem.cs:267 Report)
12:00:30.001 Info     f1800 t2 Slowest frame f1234 took 87.2 ms, 5.2x the mean: Ecs.Update 71.3 ms, Console.Update 0.1 ms, LogMetrics.Update 0.02 ms, outside the main thread's hooks 15.8 ms; GC paused 9.8 ms (LogMetricsGem.cs:479 Report)
```

## Settings

In the project's `Presets/Default.preset`, by another preset, or `--set LogMetrics.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `enabled` | `false` | Whether metrics are logged. Off, the gem does not listen, so nothing is measured for it. |
| `intervalSeconds` | `30` | How often, in seconds. 0 or less is an error, and nothing is logged. |
| `meters` | `["Magic"]` | The meters listened to, each with the meters under it: `"Magic"` is Core's `Magic.Core` and every gem's `Magic.Gems.<Gem>`. `"System.Runtime"` is .NET's own: garbage collection, memory, the JIT and the thread pool. |

All three are followed every frame: turning logging on or off, or changing the meters, takes from the next interval,
after a report of what was measured so far.

## Lines

| Instrument | Line |
| --- | --- |
| Histogram | How many, and their mean, least and most. |
| Counter, UpDownCounter | The total, and per second. |
| Gauge, and every observable instrument | The latest value: an observable counter's is already its total. |

Seconds are shown as milliseconds. An instrument recorded with the same tags in another order gets a line per order.

## Slowest frame

A mean hides a stutter, so each report ends with the slowest frame since the last one, from Core's
`magic.core.frame.duration`, `magic.core.gem.hook.duration` and `magic.core.thread.work.duration`: how long it took
against the mean, its three slowest gem hooks, the time outside the main thread's hooks (the frame loop, waiting for the
render thread, listeners, the first call's JIT) and how much of that was work handed to the main thread, the work handed
to the render thread, and how long the garbage collector paused it. The render thread's hooks and work count in the main
thread's frame they ran beside. A pause counts in whichever hook or gap it fell in, so a hook that only looks slow
because of one shows up next to the pause. Without the `Magic` meter in `meters` there is no line.

The first report's slowest frame is often the first frame, which compiles everything it calls, and the second's the
frame the first report was made in, for the same reason.

## Cost

- Each measurement is added up on the thread that records it, under that instrument's lock, and allocates nothing
  after its series' first. Core times gem hooks only while something listens, so enabling the gem turns that on too.
- The slowest frame keeps each hook's time in a list reused every frame, and copies it when a frame is the slowest
  yet; reading the garbage collector's pause time once a frame costs next to nothing.
- The lines are formatted on the main thread in `Update`, once per interval. The slowest frame takes a lock, since the
  render thread's hooks are recorded on that thread.
