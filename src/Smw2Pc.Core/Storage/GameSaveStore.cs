using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Storage;

public sealed class GameSaveStore(SnesRom rom, string? root = null)
{
    public string DirectoryPath { get; } = PlayerFiles.RomDirectory(rom.Sha256, root);
    public string SavePath => Path.Combine(DirectoryPath, "save.srm");
    public string StatePath => Path.Combine(DirectoryPath, "quicksave.state");
    private long savedRevision;

    public void LoadBattery(SnesMemoryBus bus)
    {
        ValidateRom(bus);
        if (bus.SaveRam.Length > 0 && (File.Exists(SavePath) || File.Exists(SavePath + ".bak")))
            PlayerFiles.ReadWithBackup(SavePath, bytes => { bus.LoadSaveRam(bytes); return true; });
        savedRevision = bus.SaveRamRevision;
    }

    public bool SaveBattery(SnesMemoryBus bus)
        => SaveBatteryAsync(bus).GetAwaiter().GetResult();

    // Capture on the game thread; only the immutable copy is used by the disk worker.
    // Callers await any outstanding write before replacing or restoring the machine.
    public Task<bool> SaveBatteryAsync(SnesMemoryBus bus)
    {
        ValidateRom(bus);
        if (bus.SaveRam.Length == 0 || bus.SaveRamRevision == savedRevision) return Task.FromResult(false);
        var bytes = bus.SaveRam.ToArray();
        var revision = bus.SaveRamRevision;
        return WriteBatteryAsync(bytes, revision);
    }

    private async Task<bool> WriteBatteryAsync(byte[] bytes, long revision)
    {
        await Task.Run(() => PlayerFiles.WriteAtomic(SavePath, bytes)).ConfigureAwait(false);
        savedRevision = revision;
        return true;
    }

    public void SaveState(NativeCpuRunner runner)
    {
        ValidateRom(runner.MemoryBus);
        PlayerFiles.WriteAtomic(StatePath, MachineSnapshot.Capture(runner));
    }

    public NativeCpuRunner LoadState()
    {
        var runner = PlayerFiles.ReadWithBackup(StatePath, bytes => MachineSnapshot.Restore(rom, bytes));
        // Loading an older state deliberately restores that point's cartridge saves too.
        savedRevision = -1;
        return runner;
    }

    private void ValidateRom(SnesMemoryBus bus)
    {
        if (bus.Rom.Sha256 != rom.Sha256)
            throw new ArgumentException("This save store belongs to another ROM.", nameof(bus));
    }
}
