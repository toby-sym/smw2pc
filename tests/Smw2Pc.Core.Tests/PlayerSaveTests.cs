using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Storage;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class PlayerSaveTests
{
    [TestMethod]
    public void LoRomSramMirrorsAcrossBanksAndDoesNotReplaceRomOrWorkRam()
    {
        var bus = new SnesMemoryBus(HomebrewGame.Create());
        Assert.AreEqual(2048, bus.SaveRam.Length);
        Assert.AreEqual((byte)0xFF, bus.ReadByte(0x700000));
        bus.WriteByte(0x700123, 0x56);
        Assert.AreEqual((byte)0x56, bus.ReadByte(0xF00123));
        Assert.AreEqual((byte)0x56, bus.ReadByte(0x710923));
        Assert.AreEqual(1L, bus.SaveRamRevision);
        bus.WriteByte(0x700123, 0x56);
        Assert.AreEqual(1L, bus.SaveRamRevision);
        bus.WriteByte(0x7E0123, 0xAA);
        Assert.AreEqual((byte)0xAA, bus.ReadByte(0x7E0123));
        Assert.AreEqual((byte)0x78, bus.ReadByte(0x708000));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SnapshotRestoresAndContinuesIdenticalAudioVideoAndMachineState(bool pal)
    {
        var rom = HomebrewGame.Create(pal);
        var original = new NativeCpuRunner(new SnesMemoryBus(rom));
        HomebrewGame.ConfigureSound(original.MemoryBus.Hardware);
        original.MemoryBus.Hardware.SetControllerButtons(0, SnesControllerButtons.Right);
        original.Run(21_137); // Mid-scanline, with pending sound and latched controller state.
        var snapshot = MachineSnapshot.Capture(original);
        var restored = MachineSnapshot.Restore(rom, snapshot);
        CollectionAssert.AreEqual(snapshot, MachineSnapshot.Capture(restored));
        Assert.IsTrue(original.MemoryBus.Hardware.Apu.DrainSamples().Any(s => s != 0));
        restored.MemoryBus.Hardware.Apu.DrainSamples();
        var left = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * 4];
        var right = new byte[left.Length];
        for (var frame = 0; frame < 12; frame++)
        {
            var buttons = frame % 2 == 0 ? SnesControllerButtons.Left : SnesControllerButtons.Right;
            original.MemoryBus.Hardware.SetControllerButtons(0, buttons);
            restored.MemoryBus.Hardware.SetControllerButtons(0, buttons);
            original.Run(18_000); restored.Run(18_000);
            CollectionAssert.AreEqual(original.MemoryBus.Hardware.Apu.DrainSamples(), restored.MemoryBus.Hardware.Apu.DrainSamples());
            SnesVideoRenderer.RenderFrame(original.MemoryBus.Hardware, left);
            SnesVideoRenderer.RenderFrame(restored.MemoryBus.Hardware, right);
            CollectionAssert.AreEqual(left, right);
            CollectionAssert.AreEqual(MachineSnapshot.Capture(original), MachineSnapshot.Capture(restored));
        }
    }

    [TestMethod]
    public void SnapshotRejectsCorruptionAndAnotherRom()
    {
        var rom = HomebrewGame.Create();
        var snapshot = MachineSnapshot.Capture(new NativeCpuRunner(new SnesMemoryBus(rom)));
        Assert.ThrowsExactly<InvalidDataException>(() => MachineSnapshot.Restore(HomebrewGame.Create(pal: true), snapshot));
        snapshot[48] ^= 0x20; // Payload checksum.
        Assert.ThrowsExactly<InvalidDataException>(() => MachineSnapshot.Restore(rom, snapshot));
    }

    [TestMethod]
    public async Task BackgroundBatteryWriteUsesAnImmutableCopyAndKeepsLaterChangesDirty()
    {
        var root = Path.Combine(Path.GetTempPath(), "smw2pc-async-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rom = HomebrewGame.Create();
            var bus = new SnesMemoryBus(rom);
            var store = new GameSaveStore(rom, root);
            store.LoadBattery(bus);
            bus.WriteByte(0x700000, 0x42);
            var pending = store.SaveBatteryAsync(bus);
            bus.WriteByte(0x700000, 0x77);
            Assert.IsTrue(await pending);
            Assert.AreEqual((byte)0x42, File.ReadAllBytes(store.SavePath)[0]);
            Assert.IsTrue(await store.SaveBatteryAsync(bus));
            Assert.AreEqual((byte)0x77, File.ReadAllBytes(store.SavePath)[0]);
            Assert.IsFalse(await store.SaveBatteryAsync(bus));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void BatteryAndQuickSaveRecoverFromBackupAndStayIsolatedByRom()
    {
        var root = Path.Combine(Path.GetTempPath(), "smw2pc-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rom = HomebrewGame.Create();
            var bus = new SnesMemoryBus(rom);
            var store = new GameSaveStore(rom, root);
            store.LoadBattery(bus);
            Assert.IsFalse(store.SaveBattery(bus));
            bus.WriteByte(0x700000, 0x42); store.SaveBattery(bus);
            bus.WriteByte(0x700000, 0x77); store.SaveBattery(bus);
            File.WriteAllBytes(store.SavePath, [0]);
            var fresh = new SnesMemoryBus(rom);
            store.LoadBattery(fresh);
            Assert.AreEqual((byte)0x42, fresh.ReadByte(0x700000));
            Assert.AreEqual(2048L, new FileInfo(store.SavePath).Length);
            var runner = new NativeCpuRunner(fresh);
            store.SaveState(runner);
            runner.State.X = 123; store.SaveState(runner);
            File.WriteAllBytes(store.StatePath, [0]);
            Assert.AreEqual((ushort)0, store.LoadState().State.X);
            Assert.AreNotEqual(store.DirectoryPath, new GameSaveStore(HomebrewGame.Create(pal: true), root).DirectoryPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
