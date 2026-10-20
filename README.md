# SMW2PC

SMW2PC turns your Super Mario World ROM into a portable Windows game.

## Run

Download and extract a Windows ZIP from **Releases**, or a dev artifact from **Actions**.
Open **SMW2PC.exe**, click **Add ROM**, select your `.sfc` or `.smc`, then run the generated **SMW.exe**.
The builder includes its compiler and runtime. No SDK, source checkout, or internet connection is needed.
Build errors are logged to `%LOCALAPPDATA%\SMW2PC\build.log`.

## Controls

Arrows move; **Z** jumps; **X** runs. **Enter** starts or pauses; **F2** opens settings.
**F5/F8** quick save/load; **F9** pauses the player; **F10** mutes; **F11** toggles fullscreen.
The Player tab controls volume and focus pause. Saves and preferences are kept per ROM.
[Extended 16:9 view](docs/widescreen.md) shows more terrain in supported horizontal levels.
[Player features and gameplay checks](docs/player-comfort.md) cover saves, reports, and replay scripts.

## Development and releases

Every branch push and PR produces a versioned Windows dev artifact; a tag such as `v0.1.0` publishes a GitHub Release with a ZIP and checksum.
The **Build** workflow also supports manual dev or release builds; releases require a `vX.Y.Z` tag.
`Version.props` sets the dev base version. With .NET 10, package using `./tools/package-builder.ps1`.
Run checks with `dotnet test Smw2Pc.sln` and `./tools/test-build-version.ps1`.

## Compilation

The builder generates and compiles ROM-specific CPU and audio code before writing `SMW.exe`.
The player contains no runtime ROM translator or CPU interpreter fallback. SNES hardware is modeled in host code.
This remains a proof of concept: uncompiled paths stop with a coverage error, and hardware timing and audio are approximate.
