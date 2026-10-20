# LogMetrics

Logs what the engine measures ([Core's README](../../../Core/README.md#telemetry)): once an interval the settings
set, a line per instrument and set of tags measured since the last time, then the slowest frame and where its time
went, and once more when the run ends. It provides nothing, and does nothing until its setting enables it, on the
command line with `--set` or in a preset.

Each report's lines are logged as any other line is, so each carries the log's time, level, frame and thread. An
instrument's line is its name, its tags and their values, then what was measured ([Lines](#lines)); the slowest
frame's says how long it took against the mean and where the time went ([Slowest frame](#slowest-frame)). A gem is
named in a tag and in the slowest frame's hooks by its assembly's name, the name a project lists it by.

## Settings

Each setting is documented on `LogMetricsSettings` and shown in the settings window. All are followed every frame:
turning logging on or off, or changing the meters, takes from the next interval, after a report of what was measured
so far. Off, the gem does not listen, so nothing is measured for it. An interval of 0 or less is an error, and nothing
is logged.

## Lines

| Instrument | Line |
| --- | --- |
| Histogram | How many, and their mean, least and most. |
| Counter, UpDownCounter | The total, and per second. |
| Gauge, and every observable instrument | The latest value: an observable counter's is already its total. |

Seconds are shown as milliseconds. An instrument recorded with the same tags in another order gets a line per order.

## Slowest frame

A mean hides a stutter, so each report ends with the slowest frame since the last one, from Core's frame, hook and
handed-work timings: how long it took against the mean, its slowest gem hooks, the time outside the main thread's
hooks (the frame loop, waiting for the render thread, listeners, the first call's JIT) and how much of that was work
handed to the main thread, the work handed to the render thread, and how long the garbage collector paused it. The
render thread's hooks and work count in the main thread's frame they ran beside. A pause counts in whichever hook or
gap it fell in, so a hook that only looks slow because of one shows up next to the pause. Without Core's meter among
the meters listened to there is no line.

The first report's slowest frame is often the first frame, which compiles everything it calls, and the second's the
frame the first report was made in, for the same reason.

## Cost

- Each measurement is added up on the thread that records it, under that instrument's lock, and allocates nothing
  after its series' first. Core times gem hooks only while something listens, so enabling the gem turns that on too.
- The slowest frame keeps each hook's time in a list reused every frame, and copies it when a frame is the slowest
  yet; the garbage collector's pause time is read once a frame.
- The lines are formatted on the main thread in `Update`, once per interval. The slowest frame takes a lock, since the
  render thread's hooks are recorded on that thread.
