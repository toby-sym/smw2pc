using System.Security.Cryptography;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Recompilation;

public sealed record ProofCheckpoint(int Frame, string StateSha256, int GameMode, int Level, string AudioSha256 = "");
public sealed record ProofProfile(string Name, int Frames, List<ProofCheckpoint> Checkpoints);
public sealed record ProofDocument(string RomSha256, List<ProofProfile> Profiles);

public static class ProofPlayback
{
    public static SnesControllerButtons Buttons(string profile, int frame, SnesMemoryBus bus)
    {
        if (profile == "attract" || frame < 180) return SnesControllerButtons.None;
        var mode = bus.Hardware.ReadWorkRam(0x100);
        if (mode < 0x0B)
            return frame % 60 < 3 ? SnesControllerButtons.Start : SnesControllerButtons.None;
        if (mode == 0x0E)
            return (profile == "exercise" ? SnesControllerButtons.Right : SnesControllerButtons.Left)
                | (frame % 180 is >= 120 and < 123 ? SnesControllerButtons.B : SnesControllerButtons.None);
        if (mode != 0x14)
            return frame % 90 < 3 ? SnesControllerButtons.B : SnesControllerButtons.None;
        if (bus.Hardware.ReadWorkRam(0x1426) != 0)
            return frame % 60 < 3 ? SnesControllerButtons.A | SnesControllerButtons.B
                | SnesControllerButtons.X | SnesControllerButtons.Y : SnesControllerButtons.None;
        var direction = profile == "exercise" && frame % 480 >= 360
            ? SnesControllerButtons.Left : SnesControllerButtons.Right;
        return direction | SnesControllerButtons.Y
            | (frame % 60 < 25 ? SnesControllerButtons.B : SnesControllerButtons.None);
    }

    public static ProofCheckpoint Capture(NativeCpuRunner runner, int frame, string audioSha256 = "")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var s = runner.State;
            writer.Write(s.Accumulator); writer.Write(s.X); writer.Write(s.Y);
            writer.Write(s.StackPointer); writer.Write(s.DirectPage); writer.Write(s.ProgramCounter);
            writer.Write(s.DataBank); writer.Write(s.ProgramBank); writer.Write(s.Status);
            writer.Write(s.EmulationMode); writer.Write(s.Halted); writer.Write(s.WaitingForInterrupt);
        }
        hash.AppendData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        var hardware = runner.MemoryBus.Hardware;
        hash.AppendData(runner.MemoryBus.SaveRam.Span);
        hash.AppendData(hardware.WorkRam.Span);
        hash.AppendData(hardware.VideoRam.Span);
        hash.AppendData(hardware.ColorRam.Span);
        hash.AppendData(hardware.ObjectAttributeMemory.Span);
        hash.AppendData(hardware.PpuRegisters.Span);
        hash.AppendData(hardware.Apu.AudioRam.Span);
        hash.AppendData(hardware.Apu.GetExecutionState());
        return new ProofCheckpoint(frame, Convert.ToHexString(hash.GetHashAndReset()),
            hardware.ReadWorkRam(0x100), hardware.ReadWorkRam(0x13BF), audioSha256);
    }
}
