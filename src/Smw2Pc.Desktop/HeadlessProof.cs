using System.Diagnostics;
using System.Text.Json;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;

internal static class HeadlessProof
{
    public static int Run(string[] args)
    {
        try
        {
            var verify = args.Contains("--verify");
            var frames = 2400;
            var profileName = "play";
            string? report = null;
            string? screenshot = null;
            string? romPath = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--verify": case "--headless": break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--profile": profileName = args[++i]; break;
                    case "--report": report = args[++i]; break;
                    case "--screenshot": screenshot = args[++i]; break;
                    case "--rom": romPath = args[++i]; break;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }
            SnesRom rom;
            if (romPath is not null) rom = SnesRom.Load(romPath);
            else
            {
                using var stream = typeof(Program).Assembly.GetManifestResourceStream("Smw2Pc.Desktop.CompiledRom")
                    ?? throw new InvalidOperationException("Supply --rom or build a ROM-specific player first.");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                rom = SnesRom.LoadBytes(bytes.ToArray(), "embedded.sfc");
            }
            ProofDocument proof;
            if (verify)
            {
                using var stream = typeof(Program).Assembly.GetManifestResourceStream("Smw2Pc.Desktop.RecompilationProof")
                    ?? throw new InvalidOperationException("This player has no embedded differential proof.");
                proof = JsonSerializer.Deserialize<ProofDocument>(stream)
                    ?? throw new InvalidDataException("Invalid proof document.");
                if (proof.RomSha256 != rom.Sha256) throw new InvalidDataException("Proof ROM hash does not match.");
            }
            else proof = new ProofDocument(rom.Sha256, [new ProofProfile(profileName, frames, [])]);

            var watch = Stopwatch.StartNew();
            var outcomes = new List<object>();
            foreach (var profile in proof.Profiles)
            {
                var bus = new SnesMemoryBus(rom);
                var runner = new NativeCpuRunner(bus);
                var checkpoints = profile.Checkpoints.ToDictionary(c => c.Frame);
                ProofCheckpoint? final = null;
                for (var frame = 0; frame < profile.Frames; frame++)
                {
                    bus.Hardware.SetControllerButtons(0, ProofPlayback.Buttons(profile.Name, frame, bus));
                    runner.Run(18_000);
                    bus.Hardware.Apu.DrainSamples();
                    if (checkpoints.TryGetValue(frame + 1, out var expected))
                    {
                        var actual = ProofPlayback.Capture(runner, frame + 1);
                        if (actual.StateSha256 != expected.StateSha256)
                            throw new InvalidDataException($"Differential mismatch in {profile.Name}, frame {frame + 1}, CPU ${runner.State.ProgramBank:X2}{runner.State.ProgramCounter:X4}. Expected {expected.StateSha256}, actual {actual.StateSha256}.");
                        final = actual;
                    }
                    if (runner.State.Halted) throw new InvalidOperationException($"CPU halted at frame {frame + 1}.");
                }
                final ??= ProofPlayback.Capture(runner, profile.Frames);
                if (screenshot is not null) WriteBitmap(screenshot, bus.Hardware);
                Console.WriteLine($"{profile.Name}: {profile.Frames} frames, {checkpoints.Count} matching checkpoints, game mode ${final.GameMode:X2}, {runner.CompiledInstructionsExecuted:N0} compiled main instructions, {bus.Hardware.Apu.TranslatedInstructionsExecuted:N0} compiled audio instructions.");
                outcomes.Add(new { profile.Name, profile.Frames, MatchingCheckpoints = checkpoints.Count,
                    CompiledMainInstructions = runner.CompiledInstructionsExecuted,
                    CompiledAudioInstructions = bus.Hardware.Apu.TranslatedInstructionsExecuted,
                    Final = final });
            }
            var json = JsonSerializer.Serialize(new { rom.Sha256, Recompiled = NativeCpuRunner.IsRecompiled,
                Verified = verify, RuntimeCompilerFallbacks = 0, ElapsedSeconds = watch.Elapsed.TotalSeconds, Profiles = outcomes },
                new JsonSerializerOptions { WriteIndented = true });
            if (report is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, json);
            }
            Console.WriteLine(verify ? "Differential proof passed." : "Headless execution completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void WriteBitmap(string path, SnesHardware hardware)
    {
        var pixels = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        SnesVideoRenderer.RenderFrame(hardware, pixels);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(SnesVideoRenderer.Width); writer.Write(-SnesVideoRenderer.Height);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(pixels.Length);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            writer.Write(pixels[offset + 2]); writer.Write(pixels[offset + 1]); writer.Write(pixels[offset]); writer.Write((byte)255);
        }
    }
}
