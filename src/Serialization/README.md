# Magic.Serialization

Reads and writes Magic's one JSON dialect.

- `Json.Read<T>` takes a file's bytes and returns a `Result<T>`: the value, or why the JSON doesn't fit the type.
- `Json.Write` returns the value as a `JsonElement`, or why JSON can't hold it.
- `Json.Options` is the dialect itself, for calling `JsonSerializer` directly. The doc comment on `Json` says what it
  allows and refuses.
