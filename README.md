# SMW2PC

SMW2PC turns your Super Mario World ROM into a portable Windows or Linux game.

## Run

Download the Windows ZIP or Linux x64 archive from **Releases**, or a dev artifact from **Actions**.
On Windows, open **SMW2PC.exe**. On Linux, extract the `.tar.gz` and run **SMW2PC**.
Click **Add ROM** and select your `.sfc` or `.smc` file. Run the generated **SMW.exe** (Windows) or **SMW** (Linux).

The builder includes its compiler and runtime. No SDK, source checkout, or internet connection is needed.
Build errors are logged in the SMW2PC folder under your user data directory.

## Controls

Arrows move; **Z** jumps; **X** runs. **Enter** starts or pauses; **F2** opens settings.

## Development and releases

Every branch push and pull request produces versioned Windows and Linux x64 dev artifacts.
Push a tag such as `v0.1.0` to publish a GitHub Release with Windows and Linux archives and SHA-256 checksums.
The **Build desktop packages** workflow also supports manual dev or release builds; releases require a `vX.Y.Z` tag.
`Version.props` sets the base version for dev builds.

With the .NET 10 SDK installed, package for the current OS using `./tools/package-builder.ps1`.
Run checks with `dotnet test Smw2Pc.sln` and `./tools/test-build-version.ps1`.

## Compilation

The builder generates and compiles ROM-specific CPU and audio code before writing `SMW.exe` or `SMW`.
The player contains no runtime ROM translator or CPU interpreter fallback. SNES hardware is modeled in host code.
This remains a proof of concept: uncompiled paths stop with a coverage error, and hardware timing and audio are approximate.
