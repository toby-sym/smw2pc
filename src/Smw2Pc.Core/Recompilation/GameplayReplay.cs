using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Recompilation;

public sealed record ReplayInput(int Frame, SnesControllerButtons Buttons, SnesControllerButtons PlayerTwo = SnesControllerButtons.None);
public sealed record ReplayScript(int FormatVersion, string RomSha256, int Frames, int CheckpointEvery, List<ReplayInput> Inputs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Widescreen { get; init; }
}
public sealed record ReplayCheckpoint(int Frame, string StateSha256, string VideoSha256, string AudioSha256, long AudioSampleFrames);
public sealed record ReplayResult(int FormatVersion, string RomSha256, string ScriptSha256, int Frames, List<ReplayCheckpoint> Checkpoints);

public static class GameplayReplay
{
    public static ReplayResult Run(NativeCpuRunner runner, ReplayScript script, ReplayResult? expected = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(script);
        Validate(script, runner.MemoryBus.Rom.Sha256);
        var scriptHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(script)));
        if (expected is not null && (expected.FormatVersion != 1 || expected.RomSha256 != script.RomSha256
            || expected.ScriptSha256 != scriptHash || expected.Frames != script.Frames))
            throw new InvalidDataException("The replay baseline belongs to a different ROM or input script.");
        var checkpoints = new List<ReplayCheckpoint>();
        using var audioHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        runner.MemoryBus.Widescreen.Enabled = script.Widescreen;
        var viewport = script.Widescreen ? VideoViewport.Widescreen : VideoViewport.Original;
        var pixels = new byte[viewport.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        long sampleFrames = 0;
        var nextInput = 0;
        for (var frame = 0; frame < script.Frames; frame++)
        {
            if (nextInput < script.Inputs.Count && script.Inputs[nextInput].Frame == frame)
            {
                var input = script.Inputs[nextInput++];
                runner.MemoryBus.Hardware.SetControllerButtons(0, input.Buttons);
                runner.MemoryBus.Hardware.SetControllerButtons(1, input.PlayerTwo);
            }
            runner.Run(SnesHardware.InstructionsPerFrame);
            if (runner.State.Halted) throw new InvalidDataException($"Replay CPU halted at frame {frame + 1}.");
            var samples = runner.MemoryBus.Hardware.Apu.DrainSamples();
            audioHash.AppendData(MemoryMarshal.AsBytes(samples.AsSpan()));
            sampleFrames += samples.Length / 2;
            if ((frame + 1) % script.CheckpointEvery != 0 && frame + 1 != script.Frames) continue;
            SnesVideoRenderer.RenderFrame(runner.MemoryBus.Hardware, pixels, 1f, viewport);
            var checkpoint = new ReplayCheckpoint(frame + 1, ProofPlayback.Capture(runner, frame + 1).StateSha256,
                Convert.ToHexString(SHA256.HashData(pixels)), Convert.ToHexString(audioHash.GetCurrentHash()), sampleFrames);
            var index = checkpoints.Count;
            checkpoints.Add(checkpoint);
            if (expected is not null && (index >= expected.Checkpoints.Count || checkpoint != expected.Checkpoints[index]))
                throw new InvalidDataException($"Replay mismatch at frame {frame + 1}, CPU ${runner.State.ProgramBank:X2}{runner.State.ProgramCounter:X4}. State {checkpoint.StateSha256}, video {checkpoint.VideoSha256}, audio {checkpoint.AudioSha256}, samples {checkpoint.AudioSampleFrames}.");
        }
        if (expected is not null && expected.Checkpoints.Count != checkpoints.Count)
            throw new InvalidDataException("The replay baseline has an unexpected checkpoint count.");
        return new ReplayResult(1, script.RomSha256, scriptHash, script.Frames, checkpoints);
    }

    private static void Validate(ReplayScript script, string romHash)
    {
        if (script.FormatVersion != 1 || script.RomSha256 != romHash
            || script.Frames is < 1 or > 1_000_000 || script.CheckpointEvery < 1 || script.Inputs is null)
            throw new InvalidDataException("Invalid replay script or ROM hash.");
        var previous = -1;
        foreach (var input in script.Inputs)
        {
            if (input.Frame <= previous || input.Frame < 0 || input.Frame >= script.Frames
                || ((ushort)input.Buttons & 0xF000) != 0 || ((ushort)input.PlayerTwo & 0xF000) != 0)
                throw new InvalidDataException("Replay inputs must be in frame order and contain valid SNES buttons.");
            previous = input.Frame;
        }
    }
}
