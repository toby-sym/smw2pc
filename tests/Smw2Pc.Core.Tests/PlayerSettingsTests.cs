using Smw2Pc.Core.Storage;
using SDL3;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class PlayerSettingsTests
{
    [TestMethod]
    public void RomProfilesInheritDefaultsAndPersistIndependentlyWithBackupRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "smw2pc-settings-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var global = DesktopSettings.Load(root: root);
            global.VolumePercent = 70; global.MoveLeftKey = SDL.Keycode.J; global.Save();
            var first = HomebrewGame.Create().Sha256;
            var second = HomebrewGame.Create(pal: true).Sha256;
            var profile = DesktopSettings.Load(first, root);
            Assert.AreEqual(70, profile.VolumePercent);
            Assert.AreEqual(SDL.Keycode.J, profile.MoveLeftKey);
            profile.VolumePercent = 25; profile.Save();
            profile.VolumePercent = 40; profile.Save();
            Assert.AreEqual(40, DesktopSettings.Load(first, root).VolumePercent);
            Assert.AreEqual(70, DesktopSettings.Load(second, root).VolumePercent);
            File.WriteAllText(Path.Combine(PlayerFiles.RomDirectory(first, root), "settings.json"), "broken");
            Assert.AreEqual(25, DesktopSettings.Load(first, root).VolumePercent);
            Assert.AreEqual(70, DesktopSettings.Load(root: root).VolumePercent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RecentRomsExcludeMissingFilesAndMoveReusedFilesToFront()
    {
        var root = Path.Combine(Path.GetTempPath(), "smw2pc-recents-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "one.sfc"); var b = Path.Combine(root, "two.sfc");
            File.WriteAllBytes(a, [1]); File.WriteAllBytes(b, [2]);
            RecentRoms.Add(a, root); RecentRoms.Add(b, root); RecentRoms.Add(a, root);
            CollectionAssert.AreEqual(new[] { a, b }, RecentRoms.Load(root).ToArray());
            File.Delete(b);
            CollectionAssert.AreEqual(new[] { a }, RecentRoms.Load(root).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
