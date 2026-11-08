using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.EnvironmentSnapshot;
using Snapshot = PCL.Core.Minecraft.EnvironmentSnapshot.EnvironmentSnapshot;

namespace PCL.Core.Test.Minecraft.EnvironmentSnapshot;

/// <summary>
/// 环境快照服务的行为测试：捕获与保留、加载最近健康快照、差异分类，
/// 以及模组 jar 元数据读取。
/// </summary>
[TestClass]
public sealed class EnvironmentSnapshotServiceTests
{
    private string _root = null!;
    private string _data = null!;
    private string _game = null!;
    private string _instance = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLTest", Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_root, "data");
        _game = _root;
        _instance = Path.Combine(_root, "versions", "1.20.1");
        Directory.CreateDirectory(_instance);
        Directory.CreateDirectory(_data);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响用例结果。
        }
    }

    [TestMethod]
    public void CaptureThenCompare_ReportsModConfigurationAndLoaderChanges()
    {
        SeedEnvironment();
        EnvironmentSnapshotService service = new(_data);
        Snapshot baseline = service.Capture(_instance, _game);

        Write("mods/a.jar", "AAA2");
        File.Delete(Path.Combine(_root, "config", "x.yml"));
        Write("mods/b.jar", "BBB");

        Snapshot current = service.Capture(_instance, _game);
        IReadOnlyList<EnvironmentChange> changes = EnvironmentSnapshotService.Compare(baseline, current);

        Assert.AreEqual(3, changes.Count, "应报告两处模组变化与一处配置变化。");
        Assert.AreEqual("g/mods/a.jar", changes[0].RelativePath);
        Assert.AreEqual("修改", changes[0].Kind);
        Assert.AreEqual("模组", changes[0].Category);
        Assert.AreEqual("g/mods/b.jar", changes[1].RelativePath);
        Assert.AreEqual("新增", changes[1].Kind);
        Assert.AreEqual("模组", changes[1].Category);
        Assert.AreEqual("g/config/x.yml", changes[2].RelativePath);
        Assert.AreEqual("删除", changes[2].Kind);
        Assert.AreEqual("配置/环境资源", changes[2].Category);
    }

    [TestMethod]
    public void Compare_WithNullBaseline_TreatsEverythingAsAdded()
    {
        SeedEnvironment();
        EnvironmentSnapshotService service = new(_data);
        Snapshot current = service.Capture(_instance, _game);

        IReadOnlyList<EnvironmentChange> changes = EnvironmentSnapshotService.Compare(null, current);

        Assert.AreEqual(current.Files.Count, changes.Count, "没有基线时应把全部文件视为新增。");
        Assert.IsTrue(changes.All(change => change.Kind == "新增"), "没有基线时变更类型应全部为新增。");
        Assert.IsTrue(changes.Any(change => change.Category == "启动器版本配置"), "应包含启动器版本配置分类。");
        Assert.IsTrue(changes.Any(change => change.Category == "Minecraft/加载器"), "应包含 Minecraft/加载器分类。");
    }

    [TestMethod]
    public void LoadLatestHealthy_ReturnsSavedSnapshot()
    {
        SeedEnvironment();
        EnvironmentSnapshotService service = new(_data);
        Snapshot captured = service.Capture(_instance, _game);

        Snapshot? loaded = service.LoadLatestHealthy(_instance);

        Assert.IsNotNull(loaded, "应能读回刚保存的健康快照。");
        Assert.IsTrue(loaded.Healthy, "读回的快照应保持健康标记。");
        Assert.AreEqual(captured.Files.Count, loaded.Files.Count, "读回的文件数量应一致。");
        Assert.AreEqual(_instance, loaded.InstanceFolder, "实例路径应一致。");
        Assert.AreEqual(_game, loaded.GameRoot, "游戏根应一致。");
    }

    [TestMethod]
    public void Capture_KeepsOnlyRecentSnapshots()
    {
        SeedEnvironment();
        EnvironmentSnapshotService service = new(_data);

        service.Capture(_instance, _game, keep: 2);
        service.Capture(_instance, _game, keep: 2);
        service.Capture(_instance, _game, keep: 2);

        int count = Directory.GetFiles(_data, "*.json", SearchOption.AllDirectories).Length;
        Assert.AreEqual(2, count, "保留数为 2 时只应留下最近两份快照。");
    }

    [TestMethod]
    public void EnvironmentModReader_ReadsFabricMetadataAndFamily()
    {
        string address = Path.Combine(_root, "mods", "jei-1.20.1-15.2.0.27.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(address)!);
        using (ZipArchive archive = ZipFile.Open(address, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("fabric.mod.json");
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"id\":\"jei\",\"name\":\"JEI\",\"version\":\"15.2.0.27\"}");
        }

        EnvironmentModInfo? info = EnvironmentModReader.Read(address);

        Assert.IsNotNull(info, "应能从 fabric.mod.json 读到模组信息。");
        Assert.AreEqual("jei", info.ModId);
        Assert.AreEqual("JEI", info.Name);
        Assert.AreEqual("15.2.0.27", info.Version);
        Assert.AreEqual("jei", EnvironmentModReader.Family("jei-1.20.1-15.2.0.27.jar"), "应去掉版本号后缀。");
        Assert.AreEqual("sodium", EnvironmentModReader.Family("sodium-0.5.8.jar"), "应去掉版本号后缀。");
        Assert.IsNull(EnvironmentModReader.Read(Path.Combine(_root, "missing.jar")), "不存在的 jar 应返回 null。");
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    private void SeedEnvironment()
    {
        Write("versions/1.20.1/1.20.1.json", "{\"id\":\"1.20.1\",\"type\":\"release\",\"mainClass\":\"Main\"}");
        Write("versions/1.20.1/1.20.1.jar", "jar-bytes");
        Write("versions/1.20.1/PCL/Setup.ini", "VersionRamType:2");
        Write("mods/a.jar", "AAA");
        Write("config/x.yml", "cfg");
        Write("options.txt", "fov:90");
    }

    private void Write(string relative, string text)
    {
        string address = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        string? parent = Path.GetDirectoryName(address);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(address, text);
    }
}
