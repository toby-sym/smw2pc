# SMW2PC

Native 65C816 translation runner for Super Mario World, with an SDL3 desktop
front end.

Run it with:

```sh
dotnet run --project src/Smw2Pc.Desktop
```

Click **Open ROM** and choose a local `.sfc` or `.smc` file, or a `.zip` archive
containing exactly one such file. SMW2PC checks the cartridge header, starts the
translated CPU, and displays the SNES video output. Use the arrow keys to move,
**Z** to jump, **X** to run, **Enter** for Start, **Right Shift** for Select,
**A/S** for Y/X, and **Q/W** for L/R. Press **F1** to open another ROM.

Resize the window directly or open **Settings** with **F2** to choose a window
size, aspect ratio, and smooth or sharp scaling. The render cap can be adjusted
from 15 to 240 FPS. These options affect presentation only: game logic continues
at the ROM's native PAL or NTSC rate. Settings are saved between launches.

The core translates 65C816 instruction blocks into .NET IL, which the .NET JIT
compiles for the host CPU. It can execute code from cartridge ROM and copied
WRAM. The tested Super Mario World (Europe) ROM reaches its intro, Yoshi's
Island overworld, and Yoshi's House; player movement responds in the first
level. This confirms the current boot-to-gameplay milestone, while the full game
and every gameplay mechanic still need validation. The decoder has entries for
all 256 opcode bytes in the [WDC W65C816S datasheet's opcode matrix](https://www.westerndesigncenter.com/wdc/documentation/w65c816s.pdf)
(Table 5-4). This checks opcode coverage, while instruction behavior and timing
still need broader validation.

The hardware path includes LoROM/WRAM mapping, DMA/HDMA, PPU tile, sprite and
Mode 7 output, controller input, frame-based VBlank/NMI, and a separate SPC700
audio subsystem. The SPC700 executes its own instruction stream while the main
65C816 remains translated. The APU includes the boot ROM protocol, timers,
bidirectional ports, BRR sample playback, eight DSP voices, and SDL3 stereo
output. Audio timing and DSP interpolation are approximate; this is not a
cycle-accurate sound chip implementation. PPU features, DMA timing, interrupts,
and main CPU timing also remain partial, so compatibility beyond the verified
gameplay path is not established.

Run the core tests with:

```sh
dotnet test Smw2Pc.sln
```
