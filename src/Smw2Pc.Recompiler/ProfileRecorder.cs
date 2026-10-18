using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

internal sealed class ProfileRecorder(CodeDiscovery discovery)
{
    public HashSet<(ushort Address, byte Opcode)> AudioEntries { get; } = [];

    public ProofProfile Run(SnesRom rom, string name, int frames)
    {
        var bus = new SnesMemoryBus(rom);
        var runner = new NativeCpuRunner(bus) { BlockDiscovered = discovery.AddObserved };
        bus.Hardware.Apu.InstructionObserved = (pc, opcode) => AudioEntries.Add((pc, opcode));
        var proof = new ProofProfile(name, frames, []);
        var previousMode = -1;
        for (var frame = 0; frame < frames; frame++)
        {
            bus.Hardware.SetControllerButtons(0, ProofPlayback.Buttons(name, frame, bus));
            runner.Run(18_000);
            bus.Hardware.Apu.DrainSamples();
            var mode = bus.Hardware.ReadWorkRam(0x100);
            if (mode != previousMode)
            {
                Console.WriteLine($"  {name}: frame {frame + 1}, game mode ${mode:X2}, CPU ${runner.State.ProgramBank:X2}{runner.State.ProgramCounter:X4}");
                previousMode = mode;
            }
            if ((frame + 1) % 600 == 0)
                Console.WriteLine($"  {name}: X={bus.Hardware.ReadWorkRam(0x94) | bus.Hardware.ReadWorkRam(0x95) << 8}, message={bus.Hardware.ReadWorkRam(0x1426)}, introTimer={bus.Hardware.ReadWorkRam(0x13D3)}");
            if ((frame + 1) % 60 == 0 || frame + 1 == frames)
                proof.Checkpoints.Add(ProofPlayback.Capture(runner, frame + 1));
            if (runner.State.Halted) throw new InvalidDataException($"CPU halted in profile {name} at frame {frame + 1}.");
        }
        return proof;
    }
}
