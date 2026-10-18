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

## ROM-specific static recompilation proof of concept

The standard `dotnet run` launch above is the development runner: it translates
CPU blocks at runtime and uses the .NET JIT. To build a ROM-specific player with
no runtime CPU translator or CPU opcode interpreter, run:

```powershell
.\tools\build-rom-exe.ps1 -Rom ".\Super Mario World (Europe).sfc"
```

The verified output is `artifacts\rom-exe\SMW2PC.Recompiled.exe` for Windows
x64. It is a self-contained, single-file .NET application with ReadyToRun host
code, the ROM embedded, and SDL native libraries bundled. It does not require a
separate .NET installation. The executable also works when moved on its own;
it extracts bundled native libraries and its font into the local temporary
cache when launched. Other runtime identifiers can be requested with
`-RuntimeIdentifier`, but only `win-x64` has been built and verified so far.

The builder runs three deterministic 3,600 frame profiles (`attract`, `play`,
and `exercise`), records reached CPU and SPC700 instructions, discovers static
control flow and recognized SMW inline dispatch tables, then emits specialized
C# host methods. The .NET publish step compiles those methods into the player.
For the supplied Europe ROM, the generated manifest reports 21,983 main CPU
blocks, 130 writable code variants, and 1,339 audio entries. The strict player
rejects any uncompiled code path with a coverage error; it contains no runtime
translator, CPU instruction dispatcher, SPC700 opcode interpreter, or
`Reflection.Emit` dependency.

This translates the 65C816 and SPC700 instruction execution into host code. The
SNES machine remains modeled in the player, including memory mapping, PPU,
controllers, DMA/HDMA, and audio DSP. It is not a native rewrite of SMW's game
logic, and the profiles do not prove that every game path is covered. The
reference hardware implementation also has known timing and audio limits.

By default, the build script verifies SDL video/audio/font loading, then runs
the executable against all recorded checkpoints. At each checkpoint it
compares CPU, memory, video, and audio state hashes with the reference runner.
For the current Europe ROM build, all 180 checkpoints passed and the strict
assembly audit reported no runtime translator, CPU dispatcher, audio opcode
state, or `Reflection.Emit` reference. The detailed report is
`artifacts\rom-exe\verification.json`.

Useful commands:

```powershell
# Run the ROM-specific player
.\artifacts\rom-exe\SMW2PC.Recompiled.exe

# Repeat the embedded differential proof and write a report
.\artifacts\rom-exe\SMW2PC.Recompiled.exe --verify --report .\artifacts\rom-exe\verification.json

# Render a profile without opening a window
.\artifacts\rom-exe\SMW2PC.Recompiled.exe --headless --frames 2400 --profile play --screenshot .\artifacts\play.bmp
```

The builder accepts `-ProfileFrames` to record longer runs. Use
`-SkipVerification` only when cross-publishing to a target that cannot run on
the build machine; that output is unverified. The ROM itself is not committed;
provide a local `.sfc` or `.smc` file when building.
