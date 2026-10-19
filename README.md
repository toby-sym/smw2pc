# SMW2PC

SMW2PC turns your Super Mario World ROM into a portable native game for Windows and macOS.

## Run

Download and extract the Windows or macOS build from **Releases**, or a dev artifact from **Actions**.
Open **SMW2PC.exe** on Windows or **SMW2PC.app** on macOS, click **Add ROM**, and select your `.sfc` or `.smc` file.
When the build finishes, run **SMW.exe** on Windows or open **SMW.app** on macOS from the same folder.

The builder includes its compiler and runtime. No SDK, source checkout, or internet connection is needed.
Build errors are logged to `%LOCALAPPDATA%\SMW2PC\build.log`.

## Controls

Arrows move; **Z** jumps; **X** runs. **Enter** starts or pauses; **F2** opens settings.

## Development and releases

Every branch push and pull request produces versioned Windows and macOS dev artifacts.
Push a tag such as `v0.1.0` to publish a GitHub Release with Windows, macOS Intel, and macOS Apple Silicon ZIPs and SHA-256 checksums.
The **Build desktop apps** workflow also supports manual dev or release builds; releases require a `vX.Y.Z` tag.
`Version.props` sets the base version for dev builds.

With the .NET 10 SDK installed, package the builder using `./tools/package-builder.ps1`.
Run checks with `dotnet test Smw2Pc.sln` and `./tools/test-build-version.ps1`.

## Compilation

The builder generates and compiles ROM-specific CPU and audio code before writing `SMW.exe`.
The player contains no runtime ROM translator or CPU interpreter fallback. SNES hardware is modeled in host code.
This remains a proof of concept: uncompiled paths stop with a coverage error, and hardware timing and audio are approximate.
