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
operations, and status mode changes. Many 65C816 opcodes and substantial SNES
hardware behavior are still unimplemented, so it cannot boot Super Mario World
yet.

The hardware bus foundation routes PPU register access and VRAM transfers, APU
ports, the WRAM data port, and controller input. It does not render video,
generate audio, transfer DMA, or model SNES timing and interrupts. The game is
not playable yet.

Run the core tests with:

```sh
dotnet test Smw2Pc.sln
```
