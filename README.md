# SMW2PC

SDL3 desktop starter for the Super Mario World project.

Run it with:

```sh
dotnet run --project src/Smw2Pc.Desktop
```

Click **Open ROM** and choose a local `.sfc` or `.smc` file. SMW2PC checks the
SNES cartridge header and displays the ROM title and metadata. It does not run
the game yet.

Run the core tests with:

```sh
dotnet test Smw2Pc.sln
```
