using System.Reflection;
using System.Text.Json;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Rom;
using Smw2Pc.Core.Storage;

internal static class PlayerCompatibilityReport
{
    public static string Export(SnesRom rom, NativeCpuRunner? runner, string? executionError = null)
    {
        using var manifestStream = typeof(Program).Assembly.GetManifestResourceStream("Smw2Pc.Desktop.RecompilationManifest");
        using var manifest = manifestStream is null ? null : JsonDocument.Parse(manifestStream);
        var path = Path.Combine(PlayerFiles.RomDirectory(rom.Sha256), "compatibility-report.json");
        PlayerFiles.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            PlayerVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            rom.Header.Title,
            rom.Sha256,
            rom.Header.FrameRateHz,
            SaveRamBytes = rom.Header.SaveRamSize,
            Recompiled = NativeCpuRunner.IsRecompiled,
            Compiler = manifest?.RootElement,
            ExecutionError = executionError,
            CpuAddress = runner is null ? null : $"{runner.State.ProgramBank:X2}:{runner.State.ProgramCounter:X4}",
            Cpu = runner is null ? null : new
            {
                runner.State.Accumulator,
                runner.State.X,
                runner.State.Y,
                runner.State.StackPointer,
                runner.State.DirectPage,
                runner.State.Status,
                runner.State.EmulationMode,
                runner.State.Halted,
                runner.State.WaitingForInterrupt,
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
