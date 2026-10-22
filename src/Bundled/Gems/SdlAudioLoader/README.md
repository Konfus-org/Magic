# SdlAudioLoader

Loads audio files with SDL_mixer's decoders. Provides `IAssetLoader<Audio>`.

It reads WAV, Ogg Vorbis, MP3 and FLAC ([Audios](../../Resources/Audios/README.md)). A file is decoded whole into float
samples at its own rate and channel count. It opens no audio device and works headless. SDL_mixer comes from the SDL
bindings project.
