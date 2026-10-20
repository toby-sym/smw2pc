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
    {
        ValidateRom(bus);
        if (bus.SaveRam.Length == 0 || bus.SaveRamRevision == savedRevision) return false;
        PlayerFiles.WriteAtomic(SavePath, bus.SaveRam.Span);
        savedRevision = bus.SaveRamRevision;
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
