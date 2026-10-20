using System.Text.Json;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class GameplayReplayTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OriginalHomebrewMatchesCommittedVideoAudioAndStateCheckpoints(bool pal)
    {
        var (script, baseline) = Load(pal);
        var runner = new NativeCpuRunner(new SnesMemoryBus(HomebrewGame.Create(pal)));
        var result = GameplayReplay.Run(runner, script, baseline);
        var sampleFrames = result.Checkpoints[^1].AudioSampleFrames;
        Assert.IsLessThanOrEqualTo(2.0, Math.Abs(sampleFrames - script.Frames * 32_000 / runner.MemoryBus.Rom.Header.FrameRateHz));
        Assert.IsGreaterThan(1, result.Checkpoints.Select(c => c.VideoSha256).Distinct().Count());
        Assert.AreEqual((byte)0x10, runner.MemoryBus.ReadByte(0x700000)); // Start in auto-joypad high byte.
    }

    [TestMethod]
    public void ReplayDetectsInputChangesAndRejectsOutOfOrderEvents()
    {
        var (script, baseline) = Load(false);
        var runner = new NativeCpuRunner(new SnesMemoryBus(HomebrewGame.Create()));
        script.Inputs[1] = new ReplayInput(20, SnesControllerButtons.Left);
        Assert.ThrowsExactly<InvalidDataException>(() => GameplayReplay.Run(runner, script, baseline));
        script.Inputs[1] = new ReplayInput(0, SnesControllerButtons.Left);
        Assert.ThrowsExactly<InvalidDataException>(() => GameplayReplay.Run(runner, script));
    }

    private static (ReplayScript, ReplayResult) Load(bool pal)
    {
        var prefix = Path.Combine(AppContext.BaseDirectory, "Fixtures", pal ? "homebrew-pal" : "homebrew-ntsc");
        return (JsonSerializer.Deserialize<ReplayScript>(File.ReadAllText(prefix + ".replay.json"))!,
            JsonSerializer.Deserialize<ReplayResult>(File.ReadAllText(prefix + ".expected.json"))!);
    }
}
