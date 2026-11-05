# SdlBindings

The C APIs of SDL3 and SDL_image, for the gems built on SDL. No project lists it in its `.magic` file. A gem that needs
SDL references it in its csproj.

The build fetches and builds both libraries at the tags in the csproj and generates their C# APIs under C's own names
([Native libraries](../README.md#native-libraries)). Every gem that references it uses the same SDL.
