using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.PackUpdate;

namespace PCL.Core.Test.Minecraft.PackUpdate;

/// <summary>
/// 整合包更新引擎的行为测试：Modrinth/CurseForge 清单读取、覆盖层合并、
/// 差异规划、保护路径、成功落地与失败回滚。
/// 全部使用 <c>%TEMP%\PCLTest\{guid}</c> 临时目录，用例结束后删除。
/// </summary>
[TestClass]
public sealed class PackUpdateTests
{
    private string _root = null!;
    private string _instance = null!;
    private string _work = null!;
    private string _archives = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLTest", Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "instance");
        _work = Path.Combine(_instance, "PCL");
        _archives = Path.Combine(_root, "archives");
        Directory.CreateDirectory(_instance);
        Directory.CreateDirectory(_archives);
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

    // ───────────────────────────── 读取 ─────────────────────────────

    [TestMethod]
    public void Read_MrpackIndexAndOverrides()
    {
        string modHash = Sha1("a-mod");
        JsonObject index = new()
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["name"] = "测试整合包",
            ["versionId"] = "2.0.0",
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "mods/a.jar",
                    ["hashes"] = new JsonObject { ["sha1"] = modHash },
                    ["downloads"] = new JsonArray { "https://example.invalid/mods/a.jar" }
                }
            }
        };
        string archive = CreateArchive(
            "pack.mrpack",
            ("modrinth.index.json", index.ToJsonString()),
            ("overrides/config/foo.toml", "foo"),
            ("overrides/mods/extra.jar", "extra"),
            ("client-overrides/config/client.toml", "client"));

        PackPackage package = PackPackageReader.Read(archive);

        Assert.AreEqual("测试整合包", package.Name, "应读取根字段 name。");
        Assert.AreEqual("2.0.0", package.VersionId, "应读取根字段 versionId。");
        Assert.AreEqual(Path.GetFullPath(archive), package.SourceArchive, "应记录压缩包路径供落地阶段复用。");
        Assert.AreEqual(4, package.Files.Count, "应合并清单文件与两处覆盖层。");

        Assert.AreEqual(modHash, package.Files["mods/a.jar"], "清单哈希应原样保留为小写十六进制。");
        Assert.AreEqual(Sha1("foo"), package.Files["config/foo.toml"], "overrides 内容应现场计算哈希。");
        Assert.AreEqual(Sha1("extra"), package.Files["mods/extra.jar"], "overrides 中的模组应被并入。");
        Assert.AreEqual(Sha1("client"), package.Files["config/client.toml"], "client-overrides 应被并入。");

        Assert.IsFalse(package.Files.Keys.Any(key => key.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)),
            "覆盖层前缀不应出现在文件键中。");
        Assert.IsFalse(package.Files.Keys.Any(key => key.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)),
            "客户端覆盖层前缀不应出现在文件键中。");
    }

    [TestMethod]
    public void Read_CurseForgeManifestAndOverrides()
    {
        JsonObject manifest = new()
        {
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = "CF 测试包",
            ["version"] = "1.0.0",
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["projectID"] = 1,
                    ["fileID"] = 2,
                    ["path"] = "mods/cf.jar",
                    ["required"] = true
                },
                // 官方 CurseForge 清单只有 projectID/fileID，没有 path，应被跳过。
                new JsonObject { ["projectID"] = 3, ["fileID"] = 4, ["required"] = true }
            }
        };
        string archive = CreateArchive(
            "pack.zip",
            ("manifest.json", manifest.ToJsonString()),
            ("overrides/config/cf.toml", "cf-config"),
            ("client-overrides/mods/cf-client.jar", "cf-client"));

        PackPackage package = PackPackageReader.Read(archive);

        Assert.AreEqual("CF 测试包", package.Name, "应读取 manifest 的 name。");
        Assert.AreEqual("1.0.0", package.VersionId, "没有 versionId 时应回退到 CurseForge 的 version。");
        Assert.AreEqual(3, package.Files.Count, "应合并有 path 的清单项与覆盖层。");
        Assert.AreEqual("", package.Files["mods/cf.jar"], "CurseForge 清单没有哈希时应置空字符串。");
        Assert.AreEqual(Sha1("cf-config"), package.Files["config/cf.toml"], "overrides 应现场计算哈希。");
        Assert.AreEqual(Sha1("cf-client"), package.Files["mods/cf-client.jar"], "client-overrides 应被并入。");
    }

    [TestMethod]
    public void Read_RejectsEscapingZipEntry()
    {
        JsonObject index = new() { ["name"] = "恶意包", ["files"] = new JsonArray() };
        string archive = CreateArchive(
            "evil.mrpack",
            ("modrinth.index.json", index.ToJsonString()),
            ("overrides/../evil.txt", "evil"));

        InvalidDataException error = Throws<InvalidDataException>(
            () => PackPackageReader.Read(archive),
            "覆盖层路径越界时");

        Assert.IsTrue(error.Message.Contains("..", StringComparison.Ordinal), "错误信息应指出越界的 .. 段。");
    }

    [TestMethod]
    public void Read_RejectsEscapingManifestPath()
    {
        JsonObject index = new()
        {
            ["name"] = "恶意包",
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "C:/evil.jar",
                    ["hashes"] = new JsonObject { ["sha1"] = Sha1("evil") }
                }
            }
        };
        string archive = CreateArchive("evil-path.mrpack", ("modrinth.index.json", index.ToJsonString()));

        Throws<InvalidDataException>(() => PackPackageReader.Read(archive), "清单路径带盘符时");
    }

    // ───────────────────────────── 规划 ─────────────────────────────

    [TestMethod]
    public void Plan_DetectsAddedUpdatedAndPreserved()
    {
        Dictionary<string, string> files = new()
        {
            ["mods/same.jar"] = "same-content",
            ["mods/added.jar"] = "added-content",
            ["config/foo.toml"] = "new-config"
        };
        string archive = CreateMrpack("plan.mrpack", "规划测试包", "1.2.3", files);
        PackPackage package = PackPackageReader.Read(archive);

        Write("mods/same.jar", "same-content");
        Write("config/foo.toml", "old-config");
        Write("personal/notes.txt", "my-notes");

        PackUpdatePlan plan = PackUpdatePlanner.Plan(package, _instance);

        Assert.IsFalse(plan.IsEmpty, "存在差异时计划不应为空。");
        Assert.AreEqual("规划测试包", plan.ManifestName, "计划应带上整合包名称。");
        Assert.AreEqual("1.2.3", plan.VersionId, "计划应带上整合包版本。");
        Assert.AreEqual(1, plan.Added, "只有一个本地缺失的文件。");
        Assert.AreEqual(1, plan.Updated, "只有一个内容不一致的文件。");
        Assert.AreEqual(0, plan.Removed, "规划器不会凭空产生移除项。");
        Assert.AreEqual(2, plan.Preserved, "内容一致与个人文件各算一个保留项。");

        PackUpdateEntry added = plan.Entries.Single(entry => entry.RelativePath == "mods/added.jar");
        Assert.AreEqual(PackUpdateKind.Added, added.Kind, "本地缺失应为新增。");
        Assert.AreEqual("本地不存在", added.Reason, "新增原因应明确。");
        Assert.IsNull(added.ExistingSha1, "新增文件没有本地哈希。");
        Assert.AreEqual(Sha1("added-content"), added.IncomingSha1, "新增文件应带上包内哈希。");
        Assert.IsFalse(added.PersonalModified, "新增文件不存在个人修改。");

        PackUpdateEntry updated = plan.Entries.Single(entry => entry.RelativePath == "config/foo.toml");
        Assert.AreEqual(PackUpdateKind.Updated, updated.Kind, "内容不一致应为更新。");
        Assert.AreEqual("本地内容与整合包不一致", updated.Reason, "更新原因应明确。");
        Assert.AreEqual(Sha1("old-config"), updated.ExistingSha1, "应记录本地旧哈希。");
        Assert.AreEqual(Sha1("new-config"), updated.IncomingSha1, "应记录包内新哈希。");
        Assert.IsTrue(updated.PersonalModified, "覆盖前必须标记可能存在的个人修改。");

        PackUpdateEntry same = plan.Entries.Single(entry => entry.RelativePath == "mods/same.jar");
        Assert.AreEqual(PackUpdateKind.Preserved, same.Kind, "内容一致应归为保留。");
        Assert.AreEqual("内容一致", same.Reason, "内容一致的原因应明确。");

        PackUpdateEntry personal = plan.Entries.Single(entry => entry.RelativePath == "personal/notes.txt");
        Assert.AreEqual(PackUpdateKind.Preserved, personal.Kind, "本地独有文件应保留。");
        Assert.AreEqual("个人文件或非整合包内容", personal.Reason, "个人文件的原因应明确。");

        // 先按 Kind（Added → Updated → Preserved）再按路径排序。
        CollectionAssert.AreEqual(
            new[] { "mods/added.jar", "config/foo.toml", "mods/same.jar", "personal/notes.txt" },
            plan.Entries.Select(entry => entry.RelativePath).ToArray(),
            "计划排序应为 Kind 优先、路径其次。");
    }

    [TestMethod]
    public void Plan_NeverIncludesProtectedPaths()
    {
        Dictionary<string, string> files = new()
        {
            ["options.txt"] = "fov:100",
            ["saves/world/level.dat"] = "world",
            ["logs/latest.log"] = "log",
            ["crash-reports/crash.txt"] = "crash",
            ["resourcepacks/pack.zip"] = "pack",
            ["shaderpacks/shader.zip"] = "shader",
            ["schematics/build.nbt"] = "build",
            ["PCL/config.json"] = "{}",
            ["mods/disabled.jar.disabled"] = "disabled",
            [".pcl-temp/cache.bin"] = "cache",
            ["mods/ok.jar"] = "ok"
        };
        string archive = CreateMrpack("protected.mrpack", "保护路径测试包", "1.0.0", files);
        PackPackage package = PackPackageReader.Read(archive);

        Write("options.txt", "fov:90");
        Write("saves/world/level.dat", "world");
        Write("servers.dat", "servers");
        Write("usercache.json", "users");
        Write("launcher_profiles.json", "profiles");
        Write("logs/latest.log", "log");
        Write("crash-reports/crash.txt", "crash");
        Write("mods/keep.jar", "keep");

        PackUpdatePlan plan = PackUpdatePlanner.Plan(package, _instance);

        Assert.AreEqual(2, plan.Entries.Count, "只应剩下一个新增模组与一个个人文件。");
        Assert.AreEqual(1, plan.Added, "只有 mods/ok.jar 应被新增。");
        Assert.AreEqual(1, plan.Preserved, "只有 mods/keep.jar 应被保留。");

        string[] forbidden = ["saves", "options.txt", "servers.dat", "usercache.json",
            "launcher_profiles.json", "logs", "crash-reports", "resourcepacks", "shaderpacks",
            "schematics", "PCL/", ".disabled", ".pcl-"];
        foreach (PackUpdateEntry entry in plan.Entries)
        {
            foreach (string token in forbidden)
            {
                Assert.IsFalse(
                    entry.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"保护路径不应出现在计划里：{entry.RelativePath} 命中了 {token}。");
            }
        }
    }

    // ───────────────────────────── 落地 ─────────────────────────────

    [TestMethod]
    public void Apply_WritesUpdatesAndDeletesWithoutTouchingPersonalFiles()
    {
        Dictionary<string, string> files = new()
        {
            ["config/update.toml"] = "fresh",
            ["mods/new.jar"] = "new"
        };
        string archive = CreateMrpack("apply.mrpack", "落地测试包", "3.0.0", files);
        PackPackage package = PackPackageReader.Read(archive);

        Write("config/update.toml", "old");
        Write("mods/old-author.jar", "old-author");
        Write("mods/personal.jar", "mine");

        PackUpdatePlan plan = new(
            package.Name,
            package.VersionId,
            [
                new PackUpdateEntry(
                    "config/update.toml", PackUpdateKind.Updated, "更新", Sha1("old"), Sha1("fresh"), true),
                new PackUpdateEntry(
                    "mods/new.jar", PackUpdateKind.Added, "新增", null, Sha1("new"), false),
                new PackUpdateEntry(
                    "mods/old-author.jar", PackUpdateKind.Removed, "新版已移除", Sha1("old-author"), null, false),
                new PackUpdateEntry(
                    "mods/personal.jar", PackUpdateKind.Preserved, "个人文件", null, null, false)
            ]);

        List<double> progress = [];
        PackUpdateApplyResult result = PackUpdateApplier.Apply(
            package,
            plan,
            _instance,
            _work,
            progress: progress.Add);

        Assert.AreEqual(2, result.Written, "应写入一个新增文件并覆盖一个旧文件。");
        Assert.AreEqual(1, result.Deleted, "应删除一个作者文件。");
        Assert.AreEqual(1, result.Preserved, "应保留一个个人文件。");
        Assert.AreEqual(0, result.Skipped, "所有文件内容都在压缩包中，不应跳过。");
        Assert.IsFalse(result.RolledBack, "成功落地不应标记回滚。");
        Assert.AreEqual(1.0, progress[^1], 1e-9, "最后一次进度应为 1.0。");
        Assert.IsTrue(progress.All(value => value is >= 0d and <= 1d), "进度应始终位于 0 到 1 之间。");

        Assert.AreEqual("fresh", Read("config/update.toml"), "覆盖项应写入新内容。");
        Assert.AreEqual("new", Read("mods/new.jar"), "新增项应写入磁盘。");
        Assert.IsFalse(File.Exists(Path.Combine(_instance, "mods", "old-author.jar")), "作者移除项应被删除。");
        Assert.AreEqual("mine", Read("mods/personal.jar"), "个人文件不应被触碰。");
        Assert.AreEqual(0, StageDirectories().Length, "成功后不应残留暂存目录。");
    }

    [TestMethod]
    public void Apply_RollsBackWhenTargetIsBlocked()
    {
        Dictionary<string, string> files = new()
        {
            ["config/keep.txt"] = "fresh",
            ["mods/ok.jar"] = "ok",
            ["blocked/second.txt"] = "second"
        };
        string archive = CreateMrpack("rollback.mrpack", "回滚测试包", "1.0.0", files);
        PackPackage package = PackPackageReader.Read(archive);

        Write("config/keep.txt", "old");
        // 用同名文件占住 blocked 目录的位置，让第三条写入在创建目录时失败。
        Write("blocked", "占位文件");

        PackUpdatePlan plan = new(
            package.Name,
            package.VersionId,
            [
                new PackUpdateEntry(
                    "config/keep.txt", PackUpdateKind.Updated, "更新", Sha1("old"), Sha1("fresh"), true),
                new PackUpdateEntry(
                    "mods/ok.jar", PackUpdateKind.Added, "新增", null, Sha1("ok"), false),
                new PackUpdateEntry(
                    "blocked/second.txt", PackUpdateKind.Added, "新增", null, Sha1("second"), false)
            ]);

        PackUpdateApplyException error = Throws<PackUpdateApplyException>(
            () => PackUpdateApplier.Apply(package, plan, _instance, _work),
            "目标目录被同名文件占位时");

        Assert.IsTrue(error.Result.RolledBack, "失败后应报告已回滚。");
        Assert.IsInstanceOfType<IOException>(error.InnerException, "原始异常应被保留为内部异常。");
        Assert.AreEqual("old", Read("config/keep.txt"), "被覆盖的旧文件应在回滚后恢复。");
        Assert.IsFalse(File.Exists(Path.Combine(_instance, "mods", "ok.jar")), "已写入的新文件应在回滚后删除。");
        Assert.IsTrue(File.Exists(Path.Combine(_instance, "blocked")), "占位文件应保持原样。");
        Assert.IsFalse(File.Exists(Path.Combine(_instance, "blocked", "second.txt")), "失败条目不应留下半成品。");
        Assert.AreEqual(0, StageDirectories().Length, "回滚后应清理暂存目录。");
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    private string CreateMrpack(
        string fileName,
        string packageName,
        string versionId,
        IReadOnlyDictionary<string, string> files)
    {
        JsonArray indexFiles = new();
        List<(string Path, string Content)> entries = [];
        foreach (KeyValuePair<string, string> pair in files)
        {
            indexFiles.Add(new JsonObject
            {
                ["path"] = pair.Key,
                ["hashes"] = new JsonObject { ["sha1"] = Sha1(pair.Value) },
                ["downloads"] = new JsonArray { "https://example.invalid/" + pair.Key }
            });
            entries.Add(("overrides/" + pair.Key, pair.Value));
        }

        JsonObject index = new()
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["name"] = packageName,
            ["versionId"] = versionId,
            ["files"] = indexFiles
        };
        entries.Insert(0, ("modrinth.index.json", index.ToJsonString()));
        return CreateArchive(fileName, entries.ToArray());
    }

    private string CreateArchive(string fileName, params (string Path, string Content)[] entries)
    {
        string archive = Path.Combine(_archives, fileName);
        using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach ((string path, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        return archive;
    }

    private void Write(string relative, string content)
    {
        string address = Path.Combine(_instance, relative.Replace('/', Path.DirectorySeparatorChar));
        string? parent = Path.GetDirectoryName(address);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(address, content);
    }

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(_instance, relative.Replace('/', Path.DirectorySeparatorChar)));

    private string[] StageDirectories() =>
        Directory.Exists(_work)
            ? Directory.GetDirectories(_work, ".pcl-packupdate-*")
            : [];

    private static string Sha1(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

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
