# SdlBindings

The C APIs of SDL3, SDL_image, SDL_ttf and SDL_mixer, for the gems built on SDL. No project lists it in its `.magic`
file. A gem that needs SDL references it in its csproj instead.

The build fetches and builds the four libraries at the release tags in the csproj and generates their C# APIs under
C's own names ([Native libraries](../README.md#native-libraries)). Every gem that references it uses the same SDL.
