using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.EnvironmentHistory;

namespace PCL.Core.Test.Minecraft.EnvironmentHistory;

/// <summary>
/// 环境还原点引擎的行为测试：捕获、恢复、排除规则、保留策略、事务回滚。
/// 全部使用 <c>%TEMP%\PCLTest\{guid}</c> 临时目录，用例结束后删除。
/// </summary>
[TestClass]
public sealed class EnvironmentHistoryEngineTests
{
    private string _root = null!;
    private string _data = null!;
    private string _game = null!;
    private readonly List<EnvironmentHistoryEngine> _engines = [];

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLTest", Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_root, "data");
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_game);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (EnvironmentHistoryEngine engine in _engines)
        {
            engine.BeforeRescan = null;
            engine.BeforeApply = null;
        }

        _engines.Clear();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响用例结果。
        }
    }

    // ───────────────────────────── 用例 ─────────────────────────────

    [TestMethod]
    public void CaptureThenRestore_RestoresChangedEnvironment()
    {
        EnvironmentHistoryEngine engine = CreateEngine(Path.Combine(_game, "versions", "1.20.1"));
        Write("versions/1.20.1/1.20.1.json", "{\"id\":\"1.20.1\"}");
        Write("versions/1.20.1/1.20.1.jar", "jar-v1");
        Write("versions/1.20.1/PCL/Setup.ini", "ini-v1");
        Write("versions/1.20.1/PCL/config.v1.yml", "count: 1");
        Write("mods/a.jar", "AAA");
        Write("config/x.yml", "cfg");
        Write("options.txt", "opt");
        Write("saves/world/level.dat", "world");

        string id = engine.Capture("手动还原点");
        Assert.AreEqual(32, id.Length, "还原点编号应为 32 位十六进制。");

        IReadOnlyList<EnvironmentHistoryPoint> points = engine.Points();
        Assert.AreEqual(1, points.Count, "捕获后应恰好有一个还原点。");
        Assert.AreEqual(6, points[0].FileCount, "顶层 json/jar、Setup.ini、mods、config、options.txt 应被收录。");
        Assert.IsTrue(points[0].Manual, "非自动标签应记为手动还原点。");
        Assert.AreEqual("手动还原点", points[0].Label);
        Assert.IsTrue(points[0].Bytes > 0, "还原点应记录内容总大小。");
        Assert.IsFalse(engine.HasPendingRestore, "捕获不应留下事务。");

        // 把环境改坏：改内容、删文件、加文件、动 Setup.ini。
        Write("mods/a.jar", "BROKEN");
        File.Delete(Full("config/x.yml"));
        Write("mods/extra.jar", "EXTRA");
        Write("versions/1.20.1/PCL/Setup.ini", "ini-v2");
        Write("versions/1.20.1/PCL/config.v1.yml", "count: 99");

        engine.Restore(id);

        Assert.AreEqual("AAA", Read("mods/a.jar"), "被改坏的文件应恢复成原内容。");
        Assert.AreEqual("cfg", Read("config/x.yml"), "被删掉的文件应恢复。");
        Assert.IsFalse(File.Exists(Full("mods/extra.jar")), "还原点之后新增的文件应被删除。");
        Assert.AreEqual("ini-v1", Read("versions/1.20.1/PCL/Setup.ini"), "Setup.ini 应恢复。");
        Assert.AreEqual("count: 99", Read("versions/1.20.1/PCL/config.v1.yml"),
            "config.v1.yml 不在覆盖范围内，不应被回滚（否则启动计数会倒退）。");
        Assert.AreEqual("world", Read("saves/world/level.dat"), "saves 不在覆盖范围内，不应被动过。");
        Assert.IsTrue(engine.Points().Any(point => point.Label == "恢复前自动保存" && !point.Manual),
            "恢复前应自动保存一个手动=false 的还原点。");
        Assert.IsFalse(engine.HasPendingRestore, "成功的恢复不应留下事务。");
    }

    [TestMethod]
    public void Capture_SkipsExcludedDirectoriesAndFiles()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("mods/a.jar", "AAA");
        Write("mods/sub/x.dat", "x");
        Write("mods/.pcl-cache/c.dat", "cache");
        Write("mods/waypoint_backup/y.dat", "waypoint");
        Write("config/ok.yml", "ok");
        Write("config/xaero/minimap.txt", "xaero");
        Write("config/journeymap/w.dat", "jm");
        Write("config/voxelmap/v.dat", "vm");
        Write("saves/world/level.dat", "world");
        Write("logs/latest.log", "log");
        Write("crash-reports/c.txt", "crash");
        Write("resourcepacks/p.zip", "rp");
        Write("shaderpacks/s.zip", "sp");
        Write("screenshots/a.png", "png");
        Write("options.txt", "opt");

        string id = engine.Capture("排除测试");
        EnvironmentHistoryPoint point = engine.Points().Single();
        Assert.AreEqual(4, point.FileCount, "只应剩下 mods/a.jar、mods/sub/x.dat、config/ok.yml、options.txt。");

        string manifest = File.ReadAllText(Path.Combine(engine.StoreRoot, "points", id + ".json"));
        foreach (string blocked in new[] { "saves", "logs", "crash-reports", "resourcepacks", "shaderpacks", "screenshots", "xaero", "journeymap", "voxelmap", "waypoint", ".pcl-cache" })
        {
            Assert.IsFalse(manifest.Contains(blocked, StringComparison.OrdinalIgnoreCase),
                $"清单里不应出现被排除的路径：{blocked}。");
        }
    }

    [TestMethod]
    public void Capture_AbortsWhenEnvironmentChangesDuringScan()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("mods/a.jar", "AAA");
        Write("options.txt", "opt");

        engine.BeforeRescan = () => Write("options.txt", "changed during the second scan");
        IOException failure = ExpectIo(() => engine.Capture("两遍扫描"), "扫描期间内容变化应放弃本次还原点。");

        Assert.IsTrue(failure.Message.Contains("创建还原点期间目录内容发生变化", StringComparison.Ordinal),
            "异常文案应说明目录内容发生变化，实际为：" + failure.Message);
        Assert.AreEqual(0, engine.Points().Count, "放弃捕获不应留下半个清单。");
    }

    [TestMethod]
    public void PruneAutomatic_KeepsManualPointsAndNewestAutomatic()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("options.txt", "opt");

        for (int index = 0; index < 3; index++)
        {
            engine.Capture("游戏启动前");
            Thread.Sleep(30);
        }

        for (int index = 0; index < 2; index++)
        {
            engine.Capture("更新模组前");
            Thread.Sleep(30);
        }

        engine.Capture("我的手动机录");
        Thread.Sleep(30);
        engine.Capture("我的手动机录");

        Assert.AreEqual(7, engine.Points().Count, "先确认共创建了 7 个还原点。");

        engine.PruneAutomatic(2);

        IReadOnlyList<EnvironmentHistoryPoint> left = engine.Points();
        Assert.AreEqual(4, left.Count, "应保留 2 个最新自动点 + 2 个手动点。");
        Assert.AreEqual(2, left.Count(point => point.Manual), "手动还原点不应被自动清理。");
        Assert.AreEqual(2, left.Count(point => !point.Manual && point.Label == "更新模组前"),
            "保留的自动点应是最新的两个。");
        Assert.AreEqual(3, CountDeletedManifests(engine), "被裁掉的 3 个清单应挪进 deleted 目录。");

        // 手动点永远保留：保留数为 1 时也只裁自动点。
        engine.PruneAutomatic(1);
        Assert.AreEqual(2, engine.Points().Count(point => point.Manual), "保留数为 1 时手动点仍应全部保留。");
    }

    [TestMethod]
    public void Delete_MovesManifestIntoDeletedFolder()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("options.txt", "opt");
        string first = engine.Capture("第一个");
        Thread.Sleep(30);
        string second = engine.Capture("第二个");

        engine.Delete([first]);

        Assert.AreEqual(1, engine.Points().Count, "删除后应只剩一个还原点。");
        Assert.AreEqual(second, engine.Points()[0].Id);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(engine.StoreRoot, "deleted"), first + ".json",
            SearchOption.AllDirectories).Length, "删除的清单应挪进 deleted/{批次 guid}/。");

        ExpectIo(() => engine.Delete([first]), "已删除的还原点不应能再次删除。");
    }

    [TestMethod]
    public void Restore_RefusesWhenManifestIsCorrupt()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("mods/a.jar", "AAA");
        string id = engine.Capture("清单损坏");

        Write("mods/a.jar", "BROKEN");
        File.WriteAllText(Path.Combine(engine.StoreRoot, "points", id + ".json"), "{ 这不是清单");

        IOException failure = ExpectIo(() => engine.Restore(id), "清单损坏时不应恢复。");
        Assert.IsTrue(failure.Message.Contains("还原点清单", StringComparison.Ordinal),
            "异常文案应说明清单损坏，实际为：" + failure.Message);
        Assert.AreEqual("BROKEN", Read("mods/a.jar"), "失败时不应改动任何环境文件。");
        Assert.AreEqual(0, engine.Points().Count, "损坏的清单不应出现在列表里。");
    }

    [TestMethod]
    public void Restore_RefusesWhenBlobIsMissing()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("mods/a.jar", "AAA");
        string id = engine.Capture("内容缺失");

        // 模拟数据目录被清理：删掉一个内容文件。
        string blob = Directory.GetFiles(Path.Combine(engine.StoreRoot, "objects")).First();
        File.Delete(blob);

        Write("mods/a.jar", "BROKEN");
        IOException failure = ExpectIo(() => engine.Restore(id), "内容缺失时不应恢复。");
        Assert.IsTrue(failure.Message.Contains("还原点文件缺失或损坏", StringComparison.Ordinal),
            "异常文案应说明内容缺失，实际为：" + failure.Message);
        Assert.AreEqual("BROKEN", Read("mods/a.jar"), "失败时不应改动任何环境文件。");
        Assert.IsTrue(engine.Points().Count == 1, "校验失败不应删掉还原点本身。");
    }

    [TestMethod]
    public void Restore_RollsBackAppliedOperationsWhenOneFails()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("mods/a.jar", "AAA");
        Write("mods/b.jar", "BBB");
        Write("mods/c.jar", "CCC");
        string id = engine.Capture("回滚测试");

        Write("mods/a.jar", "A2");
        Write("mods/b.jar", "B2");
        File.Delete(Full("mods/c.jar"));
        Write("mods/d.jar", "DDD");

        // 操作按相对路径排序：a.jar、b.jar、c.jar、d.jar；让第三个操作中途失败。
        engine.BeforeApply = relative =>
        {
            if (relative == "mods/c.jar") throw new IOException("模拟恢复中途失败。");
        };

        IOException failure = ExpectIo(() => engine.Restore(id), "中途失败应把异常抛给调用方。");
        Assert.IsTrue(failure.Message.Contains("模拟恢复中途失败", StringComparison.Ordinal),
            "应保留原始失败信息，实际为：" + failure.Message);

        // 已应用的两个步骤必须逆序回滚，尚未处理的步骤保持原样。
        Assert.AreEqual("A2", Read("mods/a.jar"), "a.jar 应回滚到恢复前的内容。");
        Assert.AreEqual("B2", Read("mods/b.jar"), "b.jar 应回滚到恢复前的内容。");
        Assert.IsFalse(File.Exists(Full("mods/c.jar")), "c.jar 未写入，回滚后仍应缺失。");
        Assert.AreEqual("DDD", Read("mods/d.jar"), "d.jar 尚未处理，应保持原样。");
        Assert.IsFalse(engine.HasPendingRestore, "回滚成功后事务目录应被清理。");

        // 去掉注入点后重试，应能正常恢复。
        engine.BeforeApply = null;
        engine.Restore(id);
        Assert.AreEqual("AAA", Read("mods/a.jar"));
        Assert.AreEqual("BBB", Read("mods/b.jar"));
        Assert.AreEqual("CCC", Read("mods/c.jar"));
        Assert.IsFalse(File.Exists(Full("mods/d.jar")), "重试成功后多出来的文件应被删掉。");
    }

    [TestMethod]
    public void MarkHealthy_TogglesFlagAndRaisesChanged()
    {
        EnvironmentHistoryEngine engine = CreateEngine();
        Write("options.txt", "opt");
        int changed = 0;
        engine.Changed += () => changed++;

        string id = engine.Capture("健康标记");
        Assert.AreEqual(1, changed, "捕获成功后应触发一次 Changed。");
        Assert.IsFalse(engine.Points()[0].Healthy);

        engine.MarkHealthy(id);
        Assert.IsTrue(engine.Points()[0].Healthy, "MarkHealthy 后应标记为健康。");
        Assert.AreEqual(2, changed, "MarkHealthy 成功后应触发 Changed。");

        engine.MarkHealthy(id, false);
        Assert.IsFalse(engine.Points()[0].Healthy, "应能取消健康标记。");
        Assert.AreEqual(3, changed);
    }

    [TestMethod]
    public void IsAutomaticLabel_MatchesOnlyBuiltInLabels()
    {
        foreach (string label in new[]
                 {
                     "游戏启动前", "修改游戏组件前", "更新模组前", "整合包更新前", "恢复前自动保存", "测试副本迁移前"
                 })
        {
            Assert.IsTrue(EnvironmentHistoryEngine.IsAutomaticLabel(label), "内置标签应被识别为自动：{0}", label);
        }

        foreach (string label in new[] { "我的手动机录", "游戏启动前（改）", "", " ", "手动" })
        {
            Assert.IsFalse(EnvironmentHistoryEngine.IsAutomaticLabel(label), "自定义标签不应被识别为自动：{0}", label);
        }
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    private EnvironmentHistoryEngine CreateEngine(string? versionFolder = null, string? gameRoot = null)
    {
        var engine = new EnvironmentHistoryEngine(_data, versionFolder ?? _game, gameRoot ?? _game);
        _engines.Add(engine);
        return engine;
    }

    private string Full(string relative)
    {
        return Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private void Write(string relative, string content)
    {
        string path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Full(relative));

    private static int CountDeletedManifests(EnvironmentHistoryEngine engine)
    {
        string root = Path.Combine(engine.StoreRoot, "deleted");
        return Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length : 0;
    }

    private static IOException ExpectIo(Action action, string because)
    {
        try
        {
            action();
        }
        catch (IOException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            Assert.Fail($"{because}：抛出了意外的 {ex.GetType().Name}。");
        }

        Assert.Fail(because + "：但没有抛出任何异常。");
        throw new InvalidOperationException(because);
    }
}
