# Magic.Extensions

Extension members for .NET types that every project can use.

- Call `.FireAndForget()` on a task you won't await or keep. If it faults, the error is logged at your call site and
  counts toward `--fail-on-error`.
- The log can't use these, since this project depends on it.
