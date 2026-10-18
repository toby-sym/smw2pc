# SMW2PC

SMW2PC turns your Super Mario World ROM into a portable Windows game.

## Run

Download and extract a Windows ZIP from **Releases**, or a dev artifact from **Actions**.
Open **SMW2PC.exe**, click **Add ROM**, and select your `.sfc` or `.smc` file.
When the build finishes, run **SMW.exe** from the same folder.

The builder includes its compiler and runtime. No SDK, source checkout, or internet connection is needed.
Build errors are logged to `%LOCALAPPDATA%\SMW2PC\build.log`.

## Controls

Arrows move; **Z** jumps; **X** runs. **Enter** starts or pauses; **F2** opens settings.

## Development and releases

Every branch push and pull request produces a versioned Windows dev artifact.
Push a tag such as `v0.1.0` to publish a GitHub Release with a Windows ZIP and SHA-256 checksum.
The **Build Windows** workflow also supports manual dev or release builds; releases require a `vX.Y.Z` tag.
`Version.props` sets the base version for dev builds.

With the .NET 10 SDK installed, package the builder using `./tools/package-builder.ps1`.
Run checks with `dotnet test Smw2Pc.sln` and `./tools/test-build-version.ps1`.

## Compilation

The builder generates and compiles ROM-specific CPU and audio code before writing `SMW.exe`.
The player contains no runtime ROM translator or CPU interpreter fallback. SNES hardware is modeled in host code.
This remains a proof of concept: uncompiled paths stop with a coverage error, and hardware timing and audio are approximate.
