# Magic.State

Small value types the other projects share. Magic keeps little state, so this project stays small.

- Work that can fail on content or the machine returns a `Result` or `Result<T>` with the reason. A thrown exception
  is a bug.
- `Result<T>.Value` is set once `Ok` is true, and the compiler knows it.
