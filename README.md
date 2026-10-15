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

The core translates 65C816 instruction blocks into .NET IL, which the .NET JIT
compiles for the host CPU. It can execute code from cartridge ROM and copied
WRAM. Super Mario World currently reaches its **Nintendo Presents** startup
screen; gameplay is not yet working. The decoder has entries for all 256 opcode
bytes in the [WDC W65C816S datasheet's opcode matrix](https://www.westerndesigncenter.com/wdc/documentation/w65c816s.pdf)
(Table 5-4). This checks opcode coverage, while instruction behavior and timing
still need broader validation.

The current hardware path includes basic LoROM/WRAM mapping, DMA, PPU tile and
sprite output, controller input, frame-based VBlank/NMI, and a silent APU startup
stub. It does not emulate the SPC700 audio processor or fully model PPU features,
DMA timing, interrupts, and CPU cycle timing. The CPU semantics and hardware
behavior still need broader validation before gameplay.

Run the core tests with:

```sh
dotnet test Smw2Pc.sln
```
