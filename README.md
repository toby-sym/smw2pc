# SMW2PC

SMW2PC builds a ROM-specific, statically recompiled player for Super Mario World.

## Build and run

On Windows, provide a local `.sfc` or `.smc` ROM:

```powershell
.\tools\build-rom-exe.ps1 -Rom ".\Super Mario World (Europe).sfc"
.\artifacts\rom-exe\SMW2PC.Recompiled.exe
```

The build creates a portable Windows x64 executable with the ROM and SDL libraries bundled. ROM files are not included in the repository. The player also accepts a ROM from **Open ROM** or **F1**.

## Controls

- Arrows: move; **Z**: jump; **X**: run
- **Enter**: Start; **Right Shift**: Select; **A/S**: Y/X; **Q/W**: L/R
- **F2**: display settings

The window can be resized. Display settings control pixel aspect ratio and render cap; game logic runs at the ROM's native rate. Pixels use nearest-neighbor scaling, with camera and object positions interpolated between frames.

## How it works

The build discovers ROM code paths, generates host methods for 65C816 and SPC700 instructions, then compiles them into the player. Runtime opcode emulation and translation are not used. The SNES hardware, including memory, video, controllers, DMA, and audio, is modeled by the player.

This is an experimental proof of concept. Uncompiled code paths stop with a coverage error, and sampled gameplay profiles do not prove complete game coverage. Hardware timing and audio are approximate.

Run the core tests with:

```powershell
dotnet test Smw2Pc.sln
```
