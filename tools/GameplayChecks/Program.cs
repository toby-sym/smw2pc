using System.Text.Json;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Recompiler;
using Smw2Pc.TestFixtures;

if (args.Length != 2 || args[0] is not ("--prepare" or "--record-baselines"))
    throw new ArgumentException("Usage: GameplayChecks --prepare <output> | --record-baselines <fixtures-directory>");
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true };
foreach (var pal in new[] { false, true })
{
    var region = pal ? "pal" : "ntsc";
    var rom = HomebrewRom.Create(pal);
    var script = new ReplayScript(1, rom.Sha256, 120, 20,
    [
        new(0, SnesControllerButtons.None), new(20, SnesControllerButtons.Right),
        new(40, SnesControllerButtons.Right | SnesControllerButtons.B), new(60, SnesControllerButtons.Left),
        new(80, SnesControllerButtons.None), new(100, SnesControllerButtons.Start),
    ]);
    if (args[0] == "--record-baselines")
    {
        var result = GameplayReplay.Run(new NativeCpuRunner(new SnesMemoryBus(rom)), script);
        File.WriteAllText(Path.Combine(output, $"homebrew-{region}.replay.json"), JsonSerializer.Serialize(script, options));
        File.WriteAllText(Path.Combine(output, $"homebrew-{region}.expected.json"), JsonSerializer.Serialize(result, options));
        continue;
    }
    var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    var expected = JsonSerializer.Deserialize<ReplayResult>(File.ReadAllText(Path.Combine(fixtures, $"homebrew-{region}.expected.json")))!;
    GameplayReplay.Run(new NativeCpuRunner(new SnesMemoryBus(rom)), script, expected);
    var directory = Path.Combine(output, region);
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "homebrew.sfc");
    File.WriteAllBytes(path, rom.Data.ToArray());
    File.Copy(Path.Combine(fixtures, $"homebrew-{region}.replay.json"), Path.Combine(directory, "input.json"), overwrite: true);
    File.Copy(Path.Combine(fixtures, $"homebrew-{region}.expected.json"), Path.Combine(directory, "expected.json"), overwrite: true);
    RomCompilation.Generate(path, Path.Combine(directory, "source"), frames: 60, profiles: ["attract"], log: Console.WriteLine);
}
