# Building

Pitwall is released as standalone executables, attached to each [GitHub release](https://github.com/ziofil/pitwall/releases) by the `Release Executables` workflow. This is how to build them yourself.

## Native AOT

We don't currently compile to NativeAot, due to single-file packaging constraints (our native libraries currently
need to be shipped alongside the executable rather than being embedded/statically linked).

There is support for NativeAot though, and we ensure continued support by running the AOT-specific roslyn analyzers.

## Build Commands

### Standalone Executable

```sh
# dotnet publish -r <runtime> -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:IncludeAllContentForSelfExtract=true -p:PublicRelease=true Pitwall.Console/Pitwall.Console.csproj -o pitwall-<runtime>-output
# where <runtime> is one of osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
dotnet publish -r osx-arm64 -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:IncludeAllContentForSelfExtract=true -p:PublicRelease=true Pitwall.Console/Pitwall.Console.csproj -o pitwall-osx-arm64-output
```

`IncludeNativeLibrariesForSelfExtract` and `IncludeAllContentForSelfExtract` ensures our native library dependencies are
packages inside the executable directly, instead of as lib files next to the executable. This ensures we can publish
a single executable as an artifact.

`DebugType` ensures we publish no debugging symbols, for sake of package size.

`PublicRelease` tells `Nerdbank.GitVersioning` that this is a public build, so it shouldn't use prerelease version
suffixes.

## Refreshing generated files

```sh
# The config file JSON Schema (config.schema.json), from the documentation in ConsoleOptions
dotnet run --project gen/Pitwall.CodeGenerator -- schema

# The circuit outlines, corners and pit loss times shipped in Pitwall.Data/Circuits
dotnet run --project gen/Pitwall.CodeGenerator -- circuits

# The README screenshots in docs/images, rendered from a recorded session at a given lap,
# optionally following a team (by default the leading one)
dotnet run --project gen/Pitwall.CodeGenerator -- screenshots ~/.local/share/pitwall/data/2026_Mexico_City_Race 55 "Red Bull Racing"
```
