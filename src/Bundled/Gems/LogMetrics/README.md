# LogMetrics

Writes what the engine measures to the log ([Telemetry](../../../Core/README.md#telemetry)). It provides nothing.

It is off until you turn it on, for example with `--set LogMetrics.isEnabled=true` or in a preset. Then, at a set
interval and when the run ends, it logs a line per instrument with what was measured since the last report, followed
by the slowest frame and where its time went. Its settings, such as the interval and the meters it listens to, are on
`LogMetricsSettings` and show in the settings window (F4).

- The first report's slowest frame is often the first frame of the run.
