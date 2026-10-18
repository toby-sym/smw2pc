using System.Diagnostics;
using System.Text.Json;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;
using Smw2Pc.Recompiler;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Smw2Pc.Recompiler <rom> <output-directory> [--frames 2400] [--profiles attract,play,exercise]");
    return 2;
}

try
{
    var frames = 2400;
    var profiles = new[] { "attract", "play", "exercise" };
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--frames": frames = int.Parse(args[++i]); break;
            case "--profiles": profiles = args[++i].Split(','); break;
            default: throw new ArgumentException($"Unknown option {args[i]}.");
        }
    }
    if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
    if (profiles.Any(p => p is not ("attract" or "play" or "exercise")))
        throw new ArgumentException("Profiles must be attract, play or exercise.");
    var watch = Stopwatch.StartNew();
    var rom = SnesRom.Load(args[0]);
    var directory = Path.GetFullPath(args[1]);
    var discovery = new CodeDiscovery(rom);
    var recorder = new ProfileRecorder(discovery);
    var proofs = new List<ProofProfile>();
    foreach (var profile in profiles)
    {
        Console.WriteLine($"Profiling {profile}: {frames} frames...");
        proofs.Add(recorder.Run(rom, profile, frames));
    }
    Console.WriteLine("Discovering static control flow...");
    discovery.Analyze();
    var blocks = discovery.Blocks;
    RomEmitter.Write(rom, blocks, recorder.AudioEntries, directory);
    var json = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(directory, "proof.json"), JsonSerializer.Serialize(new ProofDocument(rom.Sha256, proofs), json));
    File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
    {
        FormatVersion = 1, rom.Header.Title, rom.Sha256, FramesPerProfile = frames, Profiles = profiles,
        MainCpuBlocks = blocks.Count, WritableCodeVariants = blocks.Count(b => (b.StartAddress >> 16) is 0x7E or 0x7F),
        AudioEntries = recorder.AudioEntries.Count, RuntimeCompilerIncluded = false, InterpreterFallbackIncluded = false,
        UnresolvedStaticTransfers = discovery.UnresolvedTransfers.Order().ToArray(),
        CoverageNote = "Static closure plus recorded execution paths. Strict player rejects uncompiled code; this is not a whole-game coverage guarantee.",
    }, json));
    Console.WriteLine($"Emitted {blocks.Count:N0} main CPU blocks and {recorder.AudioEntries.Count:N0} audio entries in {watch.Elapsed.TotalSeconds:0.0}s.");
    Console.WriteLine($"Generated source and proof: {directory}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
