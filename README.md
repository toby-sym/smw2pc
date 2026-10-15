# SMW2PC

SDL3 desktop starter for the Super Mario World project.

Run it with:

```sh
dotnet run --project src/Smw2Pc.Desktop
```

Click **Open ROM** and choose a local `.sfc` or `.smc` file, or a `.zip` archive
containing exactly one such file. SMW2PC checks the SNES cartridge header and
displays the ROM title and metadata. The desktop application does not start the
game yet.

The core has an early native translation path for LoROM cartridges. It
translates decoded 65C816 instruction blocks into .NET IL, which the .NET JIT
compiles for the host CPU. The current instruction subset includes common loads,
stores, arithmetic and logic, comparisons, branches, register transfers, stack
operations, and status mode changes. The translator does not yet implement the
full 65C816 instruction set or SNES hardware, so it cannot boot Super Mario
World yet.

Run the core tests with:

```sh
dotnet test Smw2Pc.sln
```
