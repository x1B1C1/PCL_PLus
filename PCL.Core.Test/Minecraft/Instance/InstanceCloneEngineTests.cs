using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instance;

namespace PCL.Core.Test.Minecraft.Instance;

/// <summary>
/// 测试副本引擎的行为测试：复制范围、排除规则、名称校验、Setup.ini 强制键、
/// 版本清单改写、来源记录与「复制期间来源变化」检测。
/// 全部使用 <c>%TEMP%\PCLTest\{guid}</c> 临时目录，用例结束后删除。
/// </summary>
[TestClass]
public sealed class InstanceCloneEngineTests
{
    private const string SourceName = "1.20.1";
    private const string CloneName = "1.20.1 测试副本";

    private string _root = null!;
    private string _versions = null!;
    private string _source = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLTest", Guid.NewGuid().ToString("N"));
        _versions = Path.Combine(_root, "versions");
        _source = Path.Combine(_versions, SourceName);
        Directory.CreateDirectory(_source);
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

    // ───────────────────────────── 用例 ─────────────────────────────

    [TestMethod]
    public void Clone_CopiesEnvironmentAndRewritesVersionFiles()
    {
        SeedSource();
        InstanceCloneEngine engine = new(_versions);
        List<InstanceCloneProgress> progress = [];
        InstanceCloneOptions options = new() { CopyServers = true };

        long estimate = InstanceCloneEngine.EstimateBytes(_source, _root, options);
        InstanceCloneResult result = engine.Clone(
            _source,
            _root,
            CloneName,
            options,
            progress: progress.Add);

        string destination = Path.Combine(_versions, CloneName);
        Assert.AreEqual(destination, result.DestinationFolder, "副本应位于 versions 下的新目录。");
        Assert.IsTrue(Directory.Exists(destination), "副本目录应已提交。");
        Assert.AreEqual(10, result.Files, "应复制版本文件、PCL 配置、mods、config、options.txt 与 servers.dat。");
        Assert.AreEqual(estimate, result.Bytes, "实际字节数应与估算一致。");
        Assert.AreEqual(0, result.Warnings.Count, "没有链接时不应产生警告。");
        Assert.AreEqual(1.0, progress[^1].Progress, 1e-9, "最后一次进度应为 1.0。");
        Assert.AreEqual("完成", progress[^1].Stage, "最后一次进度应处于完成阶段。");
        Assert.AreEqual(result.Files, progress[^1].FilesCopied, "完成时应报告全部文件数。");

        JsonObject version = JsonNode.Parse(Read(Path.Combine(destination, CloneName + ".json")))!.AsObject();
        Assert.AreEqual(CloneName, version["id"]!.GetValue<string>(), "版本 id 应改为新名称。");
        Assert.AreEqual(CloneName, version["jar"]!.GetValue<string>(), "指向自身的 jar 应改为新名称。");
        Assert.AreEqual(
            Path.Combine(destination, "assets"),
            version["assets"]!.GetValue<string>(),
            "版本清单中的旧绝对路径应改写为新目录。");
        Assert.IsTrue(File.Exists(Path.Combine(destination, CloneName + ".jar")), "同名 jar 应改名为新名称。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, SourceName + ".jar")), "旧名称的 jar 不应残留。");

        JsonObject config = JsonNode.Parse(Read(Path.Combine(destination, "PCL", "config.json")))!.AsObject();
        Assert.AreEqual(
            destination,
            config["MinecraftInstallManifest"]!["target"]!.GetValue<string>(),
            "PCL 配置中的旧路径应改写为新目录。");
        Assert.IsTrue(config["Keep"]!.GetValue<bool>(), "PCL 配置的其它键应保留。");

        string[] setup = File.ReadAllLines(Path.Combine(destination, "PCL", "Setup.ini"));
        Assert.AreEqual(1, setup.Count(line => line == "VersionLaunchCount:0"), "启动次数应清零且只出现一次。");
        Assert.AreEqual(1, setup.Count(line => line == "VersionArgumentIndie:1"), "应强制开启独立版本参数。");
        Assert.IsTrue(setup.Contains("VersionArgumentIndieV2:True"), "应强制开启独立版本参数 V2。");
        Assert.IsTrue(setup.Contains("IsStar:False"), "副本不应被标记为收藏。");
        Assert.IsTrue(setup.Contains("CustomInfo:测试副本 · 来自 " + SourceName), "应写入来源提示。");
        Assert.IsTrue(setup.Contains("VersionRamType:2"), "白名单内的内存设置应保留。");
        Assert.IsFalse(setup.Any(line => line.StartsWith("Account:", StringComparison.Ordinal)),
            "账号信息不应进入副本。");

        InstanceCloneOrigin? origin = InstanceCloneEngine.ReadOrigin(destination);
        Assert.IsNotNull(origin, "应能读回来源记录。");
        Assert.AreEqual(_source, origin.SourceVersionFolder, "来源版本目录应记录为原目录。");
        Assert.AreEqual(_root, origin.SourceGameRoot, "来源游戏根应被记录。");
        Assert.AreEqual(SourceName, origin.SourceName, "来源名称应被记录。");
        Assert.AreEqual(CloneName, origin.CloneName, "副本名称应被记录。");
        Assert.AreEqual(DateTimeKind.Utc, origin.CreatedUtc.Kind, "来源记录时间应为 UTC。");

        JsonObject sourceVersion = JsonNode.Parse(Read(Path.Combine(_source, SourceName + ".json")))!.AsObject();
        Assert.AreEqual(SourceName, sourceVersion["id"]!.GetValue<string>(), "来源版本清单不应被修改。");
        Assert.IsTrue(File.Exists(Path.Combine(_source, SourceName + ".jar")), "来源 jar 不应被移动。");
        Assert.IsTrue(Read(Path.Combine(_source, "PCL", "Setup.ini")).Contains("Account:secret"),
            "来源 Setup.ini 不应被修改。");
    }

    [TestMethod]
    public void Clone_SkipsExcludedFilesAndOptionalFolders()
    {
        SeedSource();
        InstanceCloneEngine engine = new(_versions);

        InstanceCloneResult result = engine.Clone(_source, _root, CloneName);

        string destination = Path.Combine(_versions, CloneName);
        Assert.IsTrue(File.Exists(Path.Combine(destination, "mods", "a.jar")), "普通模组应被复制。");
        Assert.IsTrue(File.Exists(Path.Combine(destination, "mods", "sub", "b.txt")), "子目录文件应被复制。");
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "saves")), "默认不应复制存档。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "servers.dat")), "默认不应复制服务器列表。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "mods", "cache", "x.bin")), "缓存目录应被排除。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "mods", "account.json")), "账号文件应被排除。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "mods", "usernamecache.json")),
            "用户名缓存应被排除。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "mods", "tmp.tmp")), "临时文件应被排除。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "mods", "broken.log")), "日志文件应被排除。");
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "mods", ".pcl-temp")),
            "以 .pcl- 开头的目录应被排除。");
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "mods", "inner", ".git")),
            "嵌套的 .git 目录应被排除。");
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "logs")), "日志目录应被排除。");
        Assert.IsFalse(File.Exists(Path.Combine(destination, "PCL", "Environment", "secret")),
            "PCL/Environment 应被排除。");
        Assert.AreEqual(9, result.Files, "应只剩下版本文件、PCL 文件、mods 两项、config 与 options.txt。");
    }

    [TestMethod]
    public void Clone_RejectsExistingDestination()
    {
        SeedSource();
        Directory.CreateDirectory(Path.Combine(_versions, CloneName));
        InstanceCloneEngine engine = new(_versions);

        InvalidOperationException error = Throws<InvalidOperationException>(
            () => engine.Clone(_source, _root, CloneName),
            "同名版本已存在时");
        Assert.AreEqual("同名版本已存在，请使用新的测试版本名称。", error.Message);
        Assert.AreEqual(0, Directory.GetDirectories(_versions, ".pcl-clone-*").Length,
            "拒绝后不应留下暂存目录。");
    }

    [TestMethod]
    public void Clone_RejectsInvalidNames()
    {
        SeedSource();
        InstanceCloneEngine engine = new(_versions);
        string[] names =
        [
            "bad!name",
            "bad;name",
            "COM0",
            "ab~1",
            new string('a', 101)
        ];

        foreach (string name in names)
        {
            Assert.IsFalse(InstanceCloneRules.IsValidName(name), "名称应被判定为非法：" + name);
            Throws<IOException>(() => engine.Clone(_source, _root, name), "名称非法时");
            Assert.IsFalse(Directory.Exists(Path.Combine(_versions, name)), "非法名称不应创建目录：" + name);
        }

        Assert.AreEqual(0, Directory.GetDirectories(_versions, ".pcl-clone-*").Length,
            "名称校验失败后不应留下暂存目录。");
    }

    [TestMethod]
    public void Clone_ForcesSetupKeys()
    {
        SeedSource();
        Write(
            "versions/1.20.1/PCL/Setup.ini",
            "VersionRamType:2\r\nVersionArgumentIndie:0\r\nVersionArgumentIndieV2:False\r\n" +
            "IsStar:True\r\nCustomInfo:旧信息\r\nVersionLaunchCount:9\r\nUnknownKey:1\r\n");
        InstanceCloneEngine engine = new(_versions);

        InstanceCloneResult result = engine.Clone(_source, _root, CloneName);

        string[] setup = File.ReadAllLines(Path.Combine(result.DestinationFolder, "PCL", "Setup.ini"));
        Assert.AreEqual(1, setup.Count(line => line == "VersionArgumentIndie:1"), "独立版本参数应被强制为 1。");
        Assert.AreEqual(1, setup.Count(line => line == "VersionArgumentIndieV2:True"),
            "独立版本参数 V2 应被强制为 True。");
        Assert.AreEqual(1, setup.Count(line => line == "IsStar:False"), "收藏标记应被强制为 False。");
        Assert.AreEqual(1, setup.Count(line => line == "CustomInfo:测试副本 · 来自 " + SourceName),
            "来源提示应覆盖旧值。");
        Assert.AreEqual(1, setup.Count(line => line == "VersionLaunchCount:0"), "启动次数应被清零。");
        Assert.IsFalse(setup.Any(line => line == "VersionArgumentIndie:0"), "旧的强制键不应残留。");
        Assert.IsFalse(setup.Any(line => line == "IsStar:True"), "旧的收藏标记不应残留。");
        Assert.IsFalse(setup.Any(line => line == "UnknownKey:1"), "白名单外的键不应进入副本。");
        Assert.IsTrue(setup.Contains("VersionRamType:2"), "白名单内的键应保留。");
    }

    [TestMethod]
    public void Clone_DetectsSourceChangeDuringCopy()
    {
        SeedSource();
        InstanceCloneEngine engine = new(_versions);
        engine.BeforeFingerprint = () => Write("mods/a.jar", "CHANGED");

        InvalidOperationException error = Throws<InvalidOperationException>(
            () => engine.Clone(_source, _root, CloneName),
            "来源在复制期间发生变化时");

        Assert.AreEqual("在复制期间来源目录发生变化，已放弃本次测试副本。", error.Message);
        Assert.IsFalse(Directory.Exists(Path.Combine(_versions, CloneName)), "检测到变化后不应提交副本。");
        Assert.AreEqual(0, Directory.GetDirectories(_versions, ".pcl-clone-*").Length,
            "放弃后应清理暂存目录。");
        Assert.AreEqual("CHANGED", Read(Path.Combine(_root, "mods", "a.jar")), "来源文件应保持测试写入的内容。");
    }

    [TestMethod]
    public void ReadOrigin_ReturnsNullForOrdinaryFolder()
    {
        SeedSource();
        Assert.IsNull(InstanceCloneEngine.ReadOrigin(_source), "没有来源记录的目录应返回 null。");

        string clone = Path.Combine(_versions, CloneName);
        Directory.CreateDirectory(Path.Combine(clone, "PCL"));
        File.WriteAllText(
            Path.Combine(clone, "PCL", "CloneOrigin.json"),
            "{\"schema\":1,\"sourceVersionFolder\":\"" + _source.Replace("\\", "\\\\") +
            "\",\"sourceGameRoot\":\"" + _root.Replace("\\", "\\\\") +
            "\",\"sourceName\":\"" + SourceName +
            "\",\"cloneName\":\"" + CloneName +
            "\",\"createdUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
        Assert.IsNotNull(InstanceCloneEngine.ReadOrigin(clone), "字段完整的来源记录应能读回。");
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    private void SeedSource()
    {
        JsonObject version = new()
        {
            ["id"] = SourceName,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["jar"] = SourceName,
            ["assets"] = Path.Combine(_source, "assets"),
            ["libraries"] = new JsonArray()
        };
        Write("versions/1.20.1/" + SourceName + ".json", version.ToJsonString());
        Write("versions/1.20.1/" + SourceName + ".jar", "jar-bytes");
        Write(
            "versions/1.20.1/PCL/Setup.ini",
            "VersionRamType:2\r\nVersionRamCustom:4096\r\nVersionLaunchCount:7\r\n" +
            "VersionArgumentIndie:0\r\nAccount:secret\r\n");
        Write(
            "versions/1.20.1/PCL/config.json",
            new JsonObject
            {
                ["MinecraftInstallManifest"] = new JsonObject { ["target"] = _source },
                ["Keep"] = true
            }.ToJsonString());
        Write("versions/1.20.1/PCL/Logo.png", "png");

        Write("mods/a.jar", "AAA");
        Write("mods/sub/b.txt", "BBB");
        Write("mods/cache/x.bin", "cache");
        Write("mods/account.json", "account");
        Write("mods/usernamecache.json", "users");
        Write("mods/tmp.tmp", "tmp");
        Write("mods/broken.log", "log");
        Write("mods/.pcl-temp/y", "temp");
        Write("mods/inner/.git/config", "git");
        Write("config/mod.toml", "cfg");
        Write("logs/latest.log", "log");
        Write("options.txt", "fov:90");
        Write("servers.dat", "servers");
        Write("saves/world/level.dat", "world");
        Write("PCL/Environment/secret", "secret");
    }

    private void Write(string relative, string text)
    {
        string address = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        string? parent = Path.GetDirectoryName(address);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(address, text);
    }

    private string Read(string address) => File.ReadAllText(address);

    private static T Throws<T>(Action action, string because) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }
        catch (Exception error)
        {
            Assert.Fail($"{because}：抛出了意外的 {error.GetType().Name}。");
            throw;
        }

        Assert.Fail(because + "：但没有抛出任何异常。");
        throw new InvalidOperationException("不可达。");
    }
}
