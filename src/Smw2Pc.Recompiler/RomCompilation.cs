using System.Diagnostics;
using System.Text.Json;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

public static class RomCompilation
{
    public static void Generate(string romPath, string outputDirectory, int frames = 3600,
        string[]? profiles = null, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        profiles ??= ["attract", "play", "exercise"];
        if (profiles.Length == 0 || profiles.Any(p => p is not ("attract" or "play" or "exercise")))
            throw new ArgumentException("Profiles must be attract, play or exercise.", nameof(profiles));

        var watch = Stopwatch.StartNew();
        var rom = SnesRom.Load(romPath);
        var directory = Path.GetFullPath(outputDirectory);
        var discovery = new CodeDiscovery(rom);
        var recorder = new ProfileRecorder(discovery);
        var proofs = new List<ProofProfile>();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke($"Profiling {profile}: {frames} frames...");
            proofs.Add(recorder.Run(rom, profile, frames, log, cancellationToken));
        }
        log?.Invoke("Discovering static control flow...");
        discovery.Analyze();
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = discovery.Blocks;
        log?.Invoke("Generating compiled game code...");
        RomEmitter.Write(rom, blocks, recorder.AudioEntries, directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "proof.json"), JsonSerializer.Serialize(new ProofDocument(rom.Sha256, proofs), json));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            rom.Header.Title,
            rom.Sha256,
            FramesPerProfile = frames,
            Profiles = profiles,
            MainCpuBlocks = blocks.Count,
            WritableCodeVariants = blocks.Count(b => (b.StartAddress >> 16) is 0x7E or 0x7F),
            AudioEntries = recorder.AudioEntries.Count,
            AudioDiscovery = "Observed SPC700 instructions plus every SPC RAM byte value captured at proof checkpoints",
            RuntimeCompilerIncluded = false,
            InterpreterFallbackIncluded = false,
            discovery.RecognizedSmwDispatchers,
            InferredPointerTables = discovery.InferredPointerTables.Order().ToArray(),
            UnresolvedStaticTransfers = discovery.UnresolvedTransfers.Order().ToArray(),
            CoverageNote = "Static closure plus recorded execution paths. Strict player rejects uncompiled code; this is not a whole-game coverage guarantee.",
        }, json));
        log?.Invoke($"Emitted {blocks.Count:N0} main CPU blocks and {recorder.AudioEntries.Count:N0} audio entries in {watch.Elapsed.TotalSeconds:0.0}s.");
    }
}
