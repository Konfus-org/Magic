# SdlAudioLoader

Audio files to samples, through SDL_mixer's decoders, called through its generated API
([Gems](../README.md#native-libraries)): provides `IAssetLoader<Audio>`. What an audio file is to the engine is in
[Audios](../../Resources/Audios/README.md). SDL_mixer and SDL come from the SDL bindings project, so SDL_mixer binds to
the one SDL every gem shares ([Sharing a library](../README.md#sharing-a-library)).

## Behaviour

- SDL_mixer starts in the gem's constructor and stops in `Dispose`, once the files decoding on workers are done; a
  file asked for after that fails.
- A file is decoded whole as it loads, by SDL_mixer's decoder for its format, into floats at the rate and with the
  channels the file has: nothing is resampled or mixed down here, as whatever plays it converts it as it plays. A
  frame a file cut short leaves half written is dropped.
- Decoders are SDL_mixer's to run on any thread, so files decode in parallel, each on the worker that loads it.
- Starting SDL_mixer readies its decoders and opens no audio device, so files load on a machine with none, and
  headless.
