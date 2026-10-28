# Magic.Debugging

The log, asserts and crashes. Every other project uses them.

## The log

`Log` takes a line from any thread at one of its levels. `Log.Verbosity` sets which levels are kept, and
`Log.ErrorCount` counts errors.

- An `ILogSink` sends the lines somewhere, such as the terminal or a file.
- `Log` keeps the last lines logged in memory, and every sink added gets them first.
- Nothing adds a sink yet, so nothing is printed; a crash file still has the lines.

## Asserts and crashes

`Assert.True` and `Assert.False` check state that cannot be, in every build. A failed assert, or an exception nothing
caught, crashes: Magic logs it, writes the message, the stack and the last lines logged to `Logs/<time>.crash` next to
itself, breaks an attached debugger and ends.
