using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Saves.Backups;

namespace PCL.Core.Test.Minecraft.Saves.Backups;

[TestClass]
public class WorldBackupServiceTests
{
    private string _root = null!;
    private string _gameFolder = null!;
    private string _backupRoot = null!;
    private TestHost _host = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLTest", Guid.NewGuid().ToString("N"));
        _gameFolder = Path.Combine(_root, "game");
        _backupRoot = Path.Combine(_root, "backups");
        Directory.CreateDirectory(Path.Combine(_gameFolder, "saves"));
        Directory.CreateDirectory(_backupRoot);
        _host = new TestHost(_backupRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言结果。
        }
    }

    [TestMethod]
    public void CaptureAndRestoreRoundTrip()
    {
        string world = CreateWorld("World A", "v1");
        var service = new WorldBackupService(_host);

        string id = service.CaptureWorld(_gameFolder, "World A", "manual", 5, CancellationToken.None);
        Assert.IsTrue(Regex.IsMatch(id, "^[0-9a-f]{32}$"), "备份编号应为 32 位小写十六进制。");

        IReadOnlyList<WorldBackupPoint> points = service.Points(_gameFolder);
        Assert.AreEqual(1, points.Count);
        Assert.AreEqual("World A", points[0].World);
        Assert.AreEqual("World A", points[0].Label);
        Assert.IsTrue(points[0].Manual, "manual 原因应被识别为手动备份。");
        Assert.AreEqual(2, points[0].FileCount, "session.lock 不应进入备份。");
        Assert.IsTrue(points[0].Directories.Contains("empty"), "空目录应被记录。");

        File.WriteAllText(Path.Combine(world, "new.txt"), "new");
        File.Delete(Path.Combine(world, "region", "r.0.0.mca"));
        Directory.Delete(Path.Combine(world, "empty"));

        string? undo = service.Restore(_gameFolder, id, 5, CancellationToken.None);
        Assert.IsNotNull(undo, "恢复前应自动备份当前世界。");
        Assert.IsFalse(File.Exists(Path.Combine(world, "new.txt")), "恢复后不应保留备份之外的文件。");
        Assert.IsTrue(File.Exists(Path.Combine(world, "region", "r.0.0.mca")));
        Assert.IsTrue(Directory.Exists(Path.Combine(world, "empty")), "空目录应被重建。");
        Assert.IsFalse(File.Exists(Path.Combine(world, "session.lock")), "session.lock 不应被还原。");
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(world, "level.dat")));

        service.Restore(_gameFolder, undo!, 5, CancellationToken.None);
        Assert.IsTrue(File.Exists(Path.Combine(world, "new.txt")), "恢复自动备份应回到恢复前状态。");
        Assert.IsFalse(File.Exists(Path.Combine(world, "region", "r.0.0.mca")));
    }

    [TestMethod]
    public void RetentionKeepsManualBackups()
    {
        CreateWorld("World A", "v1");
        CreateWorld("World B", "b1");
        var service = new WorldBackupService(_host);

        service.CaptureWorld(_gameFolder, "World A", "正常退出后", 1, CancellationToken.None);
        service.CaptureWorld(_gameFolder, "World A", "正常退出后", 1, CancellationToken.None);
        service.CaptureWorld(_gameFolder, "World A", "manual", 1, CancellationToken.None);

        IReadOnlyList<WorldBackupPoint> points = service.Points(_gameFolder);
        Assert.AreEqual(2, points.Count, "自动备份应裁到 1 份，手动备份不计入上限。");
        Assert.AreEqual(1, points.Count(point => point.Manual));
        Assert.AreEqual(1, points.Count(point => !point.Manual));

        service.CaptureWorld(_gameFolder, "World B", "正常退出后", 1, CancellationToken.None);
        Assert.AreEqual(3, service.Points(_gameFolder).Count, "裁剪不应影响其它世界。");
    }

    [TestMethod]
    public void CaptureDetectsWorldChangeAndCleansStage()
    {
        string world = CreateWorld("World A", "v1");
        _host.OnWorldLabel = folder =>
        {
            File.WriteAllText(Path.Combine(folder, "level.dat"), "changed-after-scan");
            return "World A";
        };

        var service = new WorldBackupService(_host);
        IOException error = AssertThrows<IOException>(() =>
            service.CaptureWorld(_gameFolder, "World A", "manual", 5, CancellationToken.None));

        Assert.IsTrue(error.Message.Contains("存档在备份期间发生变化"), error.Message);
        Assert.AreEqual(0, service.Points(_gameFolder).Count);
        Assert.AreEqual(0, service.BusyCount, "失败后应释放忙状态。");
        string store = service.Store(_gameFolder);
        Assert.AreEqual(0, Directory.GetDirectories(store, ".pcl-backup-*").Length, "不应留下临时目录。");
        Assert.AreEqual("changed-after-scan", File.ReadAllText(Path.Combine(world, "level.dat")));
    }

    [TestMethod]
    public void DeleteMovesPointToDeletedFolder()
    {
        CreateWorld("World A", "v1");
        var service = new WorldBackupService(_host);
        string id = service.CaptureWorld(_gameFolder, "World A", "manual", 5, CancellationToken.None);

        string? deleted = service.DeletePoints(_gameFolder, [id]);
        Assert.IsNotNull(deleted);
        Assert.IsTrue(Directory.Exists(Path.Combine(deleted!, id)));
        Assert.AreEqual(0, service.Points(_gameFolder).Count);
        AssertThrows<IOException>(() => service.Point(_gameFolder, id));
    }

    [TestMethod]
    public void PathEscapeIsRejected()
    {
        CreateWorld("World A", "v1");
        var service = new WorldBackupService(_host);

        AssertThrows<IOException>(() =>
            service.CaptureWorld(_gameFolder, "..\\World A", "manual", 5, CancellationToken.None));
        AssertThrows<IOException>(() =>
            service.CaptureWorld(_gameFolder, "World A", "manual", 0, CancellationToken.None));
        AssertThrows<IOException>(() => service.Point(_gameFolder, "../points"));
        AssertThrows<IOException>(() => service.Point(_gameFolder, new string('A', 32)));
    }

    [TestMethod]
    public void CorruptManifestIsSkipped()
    {
        CreateWorld("World A", "v1");
        var service = new WorldBackupService(_host);
        service.CaptureWorld(_gameFolder, "World A", "manual", 5, CancellationToken.None);

        string store = service.Store(_gameFolder);
        string corrupt = Path.Combine(store, "points", new string('a', 32));
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "manifest.json"), "{ this is not json");

        IReadOnlyList<WorldBackupPoint> points = service.Points(_gameFolder);
        Assert.AreEqual(1, points.Count, "损坏的记录应被跳过而不是抛出。");
        Assert.IsTrue(_host.Warnings.Count > 0, "跳过损坏记录时应记录警告。");
    }

    private string CreateWorld(string name, string content)
    {
        string world = Path.Combine(_gameFolder, "saves", name);
        Directory.CreateDirectory(Path.Combine(world, "region"));
        Directory.CreateDirectory(Path.Combine(world, "empty"));
        File.WriteAllText(Path.Combine(world, "level.dat"), content);
        File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "region-" + content);
        File.WriteAllText(Path.Combine(world, "session.lock"), "lock");
        return world;
    }

    private static TException AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            Assert.Fail($"期望 {typeof(TException).Name}，实际抛出 {exception.GetType().Name}：{exception.Message}");
        }

        Assert.Fail($"期望 {typeof(TException).Name}，但没有抛出异常。");
        return null!;
    }

    private sealed class TestHost : IWorldBackupHost
    {
        internal TestHost(string backupRoot) => BackupRoot = backupRoot;

        public string BackupRoot { get; }

        public Func<string, string?>? OnWorldLabel { get; set; }

        public List<string> Warnings { get; } = [];

        public List<string> Hints { get; } = [];

        public string? WorldLabel(string worldFolder) =>
            OnWorldLabel?.Invoke(worldFolder) ?? Path.GetFileName(worldFolder);

        public string FormatSize(long bytes) => bytes + " B";

        public void Warn(Exception exception, string message) => Warnings.Add(message + "：" + exception.Message);

        public void Hint(string message, bool error) => Hints.Add(message);

        public void Dispatch(Action action) => action();

        public void NotifyChanged()
        {
        }

        public void OpenFolder(string path)
        {
        }

        public IReadOnlyList<WorldBackupWatcher> Watchers() => [];
    }
}
