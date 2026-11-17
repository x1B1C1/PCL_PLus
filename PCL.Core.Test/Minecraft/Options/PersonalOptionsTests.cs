using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Options;

namespace PCL.Core.Test.Minecraft.Options;

/// <summary>
/// 「个人游戏习惯」的规则、数据层与编排测试：逐行保真合并、视场角与键鼠值互转、
/// 缺失文件不创建、写入前被外部修改则放弃、会话补采集与 JSON 结构。
/// 全部使用 <c>%TEMP%\PCLTest\{guid}</c> 临时目录，用例结束后删除。
/// </summary>
[TestClass]
public sealed class PersonalOptionsTests
{
    private string _root = null!;
    private string _data = null!;
    private string _game = null!;

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
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响用例结果。
        }
    }

    // ─────────────────────────── 逐行保真合并 ───────────────────────────

    [TestMethod]
    public void MergeText_KeepsLineOrderCrlfAndSkipsUnknownKeys()
    {
        const string original = "version:3465\r\nfov:90\r\ngraphicsMode:fancy\r\n";
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["fov"] = "90",
            ["mouseSensitivity"] = "0.5",
            // autoJump 与 bobView 在目标文件里不存在，且不属于允许注入的白名单，应该被忽略。
            ["autoJump"] = "false",
            ["bobView"] = "false"
        };
        HashSet<string> keys = new(values.Keys, StringComparer.Ordinal);

        string merged = PersonalOptionsRules.MergeText(original, values, true, keys, _ => true,
            fovDegrees: true);

        Assert.AreEqual("version:3465\r\nfov:90\r\ngraphicsMode:fancy\r\nmouseSensitivity:0.5\r\n", merged,
            "应保留行序与 CRLF，只在末尾追加允许注入的 mouseSensitivity。");
        Assert.IsFalse(merged.Contains("autoJump", StringComparison.Ordinal), "目标文件没有的非白名单键不应被注入。");
        Assert.IsFalse(merged.Contains("bobView", StringComparison.Ordinal), "目标文件没有的非白名单键不应被注入。");
        Assert.IsTrue(merged.IndexOf("fov:", StringComparison.Ordinal) <
                      merged.IndexOf("graphicsMode:", StringComparison.Ordinal), "原有行的先后顺序必须保持。");
        Assert.AreEqual(4, merged.Replace("\r\n", "\n").Split('\n').Length - 1, "应只新增一行。");
    }

    [TestMethod]
    public void MergeText_KeepsMissingFinalNewline()
    {
        const string original = "version:3465\nresourcePacks:[\"vanilla\"]";
        string merged = PersonalOptionsRules.MergeText(original, new Dictionary<string, string>(),
            true, new HashSet<string>(), _ => true);

        Assert.AreEqual(original, merged, "没有可合并内容时结果必须与原文件逐字节一致，且保留「没有末尾换行」。");
    }

    [TestMethod]
    public void MergeText_DoesNotTouchLinesOutsideSelectedKeys()
    {
        const string original = "key_key.forward:87\nkey_key.attack:-100\n";
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["key_key.forward"] = "key.keyboard.w",
            ["key_key.attack"] = "key.mouse.left"
        };
        HashSet<string> keys = ["key_key.forward"];

        string merged = PersonalOptionsRules.MergeText(original, values, true, keys, _ => true);

        Assert.AreEqual("key_key.forward:key.keyboard.w\nkey_key.attack:-100\n", merged,
            "选中的键按档案改写，未进入 key 集合的键即使出现在档案里也必须保持原值。");
    }

    // ─────────────────────────── 视场角 ───────────────────────────

    [TestMethod]
    public void Fov_ConvertsBetweenNormalizedAndDegrees()
    {
        Assert.AreEqual("70", PersonalOptionsRules.CanonicalValue("fov", "0"), "0 应归一为 70 度。");
        Assert.AreEqual("110", PersonalOptionsRules.CanonicalValue("fov", "1"), "1 应归一为 110 度。");
        Assert.AreEqual("30", PersonalOptionsRules.CanonicalValue("fov", "-1"), "-1 应归一为 30 度。");
        Assert.AreEqual("90", PersonalOptionsRules.CanonicalValue("fov", "90"), "角度制的值应原样保留。");
        Assert.IsNull(PersonalOptionsRules.CanonicalValue("fov", "20"), "20 既不在 -1..1 也不在 30..110，应拒绝。");
        Assert.IsNull(PersonalOptionsRules.CanonicalValue("fov", "abc"), "非数字应拒绝。");

        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["fov"] = "90" };
        HashSet<string> keys = ["fov"];

        Assert.AreEqual("version:1\nfov:90\n",
            PersonalOptionsRules.MergeText("version:1\n", values, true, keys, _ => true, fovDegrees: true),
            "现代版本应写入角度制。");
        Assert.AreEqual("version:1\nfov:0.5\n",
            PersonalOptionsRules.MergeText("version:1\n", values, true, keys, _ => true, fovDegrees: false),
            "旧版本应写入 (90-70)/40 = 0.5。");
    }

    [TestMethod]
    public void Fov_ExistingNormalizedFileKeepsNormalizedForm()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["fov"] = "90" };
        string merged = PersonalOptionsRules.MergeText("fov:-1\n", values, true, new HashSet<string>(["fov"]),
            _ => true, fovDegrees: true);

        Assert.AreEqual("fov:0.5\n", merged, "目标文件已有 -1..1 形式的 fov 时，即使参数要求角度制也应沿用原形式。");
    }

    // ─────────────────────────── 键鼠互转 ───────────────────────────

    [TestMethod]
    public void KeyboardAndMouse_ConvertBothWays()
    {
        Assert.AreEqual("key.mouse.left", PersonalOptionsRules.CanonicalValue("key_key.forward", "-100"));
        Assert.AreEqual("key.mouse.right", PersonalOptionsRules.CanonicalValue("key_key.forward", "-99"));
        Assert.AreEqual("key.mouse.middle", PersonalOptionsRules.CanonicalValue("key_key.forward", "-98"));
        Assert.AreEqual("key.mouse.4", PersonalOptionsRules.CanonicalValue("key_key.forward", "-97"));
        Assert.AreEqual("key.keyboard.space", PersonalOptionsRules.CanonicalValue("key_key.forward", "57"));
        Assert.AreEqual("key.keyboard.a", PersonalOptionsRules.CanonicalValue("key_key.forward", "30"));
        Assert.AreEqual("key.keyboard.unknown", PersonalOptionsRules.CanonicalValue("key_key.forward", "0"));
        Assert.AreEqual("key.mouse.right", PersonalOptionsRules.CanonicalValue("key_key.forward", "key.mouse.right"));
        Assert.IsNull(PersonalOptionsRules.CanonicalValue("key_key.forward", "-101"),
            "超出旧版鼠标键码范围的数字应拒绝。");

        Assert.AreEqual("-100", PersonalOptionsRules.ForVersion("key_key.forward", "key.mouse.left", false));
        Assert.AreEqual("57", PersonalOptionsRules.ForVersion("key_key.forward", "key.keyboard.space", false));
        Assert.AreEqual("-97", PersonalOptionsRules.ForVersion("key_key.forward", "key.mouse.4", false));
        Assert.AreEqual("key.keyboard.space",
            PersonalOptionsRules.ForVersion("key_key.forward", "key.keyboard.space", true),
            "现代版本应保留字符串绑定。");
        Assert.AreEqual("key_key.swapHands",
            PersonalOptionsRules.KeyForVersion("key_key.swapOffhand", false),
            "1.13 之前应写回 swapHands。");

        string merged = PersonalOptionsRules.MergeText("key_key.forward:57\r\n",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key_key.forward"] = "key.keyboard.tab" },
            false, new HashSet<string>(["key_key.forward"]), _ => true);
        Assert.AreEqual("key_key.forward:15\r\n", merged, "旧版本应把 key.keyboard.tab 写成数字码 15。");
    }

    [TestMethod]
    public void MergeText_WritesLegacySwapHandsKeyName()
    {
        string merged = PersonalOptionsRules.MergeText("version:1\n",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key_key.swapOffhand"] = "key.keyboard.g" },
            false, new HashSet<string>(["key_key.swapOffhand"]), _ => true);

        Assert.AreEqual("version:1\nkey_key.swapHands:34\n", merged,
            "旧版本应以 swapHands 追加按键，值为 g 的旧版数字码 34。");
    }

    // ─────────────────────────── 准备阶段 ───────────────────────────

    [TestMethod]
    public void Prepare_DoesNotCreateMissingOptionsFile()
    {
        PersonalOptionsStore store = new(_data);
        store.WriteProfile(new Dictionary<string, string>(StringComparer.Ordinal) { ["fov"] = "90" });
        PersonalOptionsFlow flow = new(store);

        PersonalOptionsResult result = flow.Prepare(_game, _game,
            [PersonalOptionsCategory.View], CancellationToken.None);

        Assert.IsTrue(result.Skipped, "options.txt 不存在时必须跳过。");
        Assert.IsNull(result.Session, "跳过时不应产生会话。");
        Assert.IsFalse(File.Exists(Path.Combine(_game, "options.txt")), "绝不能创建 options.txt。");
        Assert.IsFalse(result.Written, "跳过时不应写文件。");
    }

    [TestMethod]
    public void Prepare_DropsKeyBindingsWhenVersionInvalid()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "key_key.forward:57\nmouseSensitivity:0.5\n");

        PersonalOptionsStore store = new(_data);
        store.WriteProfile(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key_key.forward"] = "key.keyboard.tab",
            ["mouseSensitivity"] = "1.0"
        });
        PersonalOptionsFlow flow = new(store);

        PersonalOptionsResult result = flow.Prepare(new PersonalOptionsTarget
        {
            InstancePath = _game,
            GameFolder = _game,
            VersionValid = false,
            VanillaMajor = 0
        }, [PersonalOptionsCategory.Keys, PersonalOptionsCategory.Mouse], CancellationToken.None);

        Assert.AreEqual(PersonalOptionsStatus.Applied, result.Status);
        string merged = File.ReadAllText(path);
        Assert.AreEqual("key_key.forward:57\nmouseSensitivity:1.0\n", merged,
            "版本信息无效时不能改写按键绑定，其余类别仍应同步。");
        Assert.IsFalse(result.AppliedKeys.Contains("key_key.forward"), "按键不应进入会话基线。");
    }

    // ─────────────────────────── 写入冲突 ───────────────────────────

    [TestMethod]
    public void WriteOptions_AbortsWhenFileChangedAfterRead()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "fov:70\n");
        PersonalOptionsStore store = new(_data);

        PersonalOptionsSnapshot snapshot = store.ReadOptions(_game)!;
        File.WriteAllText(path, "fov:80\n");

        bool threw = false;
        try
        {
            store.WriteOptions(_game, "fov:90\n", snapshot.Hash, snapshot.HasBom);
        }
        catch (IOException error)
        {
            threw = true;
            Assert.IsTrue(error.Message.Contains("配置文件在写入前被修改", StringComparison.Ordinal),
                $"异常消息应说明写入前被修改，实际为：{error.Message}");
        }
        Assert.IsTrue(threw, "写入前文件被外部修改时必须抛出异常。");
        Assert.AreEqual("fov:80\n", File.ReadAllText(path), "放弃写入后原文件内容必须保持不变。");

        PersonalOptionsSnapshot again = store.ReadOptions(_game)!;
        Assert.IsTrue(store.WriteOptions(_game, "fov:90\n", again.Hash, again.HasBom));
        Assert.AreEqual("fov:90\n", File.ReadAllText(path));
    }

    [TestMethod]
    public void Prepare_AbortsWhenFileChangedBetweenReadAndWrite()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "fov:70\n");

        TamperingStore store = new(_data);
        store.WriteProfile(new Dictionary<string, string>(StringComparer.Ordinal) { ["fov"] = "90" });
        PersonalOptionsFlow flow = new(store);

        bool threw = false;
        try
        {
            flow.Prepare(_game, _game, [PersonalOptionsCategory.View], CancellationToken.None);
        }
        catch (IOException error)
        {
            threw = true;
            Assert.IsTrue(error.Message.Contains("配置文件在写入前被修改", StringComparison.Ordinal),
                $"异常消息应说明写入前被修改，实际为：{error.Message}");
        }
        Assert.IsTrue(threw, "读取后被外部修改时，准备阶段必须放弃写入并抛出。");
        Assert.IsTrue(File.ReadAllText(path).Contains("achievements:1", StringComparison.Ordinal),
            "外部写入的内容不应被启动器覆盖。");
    }

    // ─────────────────────────── 会话与补采集 ───────────────────────────

    [TestMethod]
    public void RecoverExitedSessions_CapturesDeadSession()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "fov:90\nmouseSensitivity:0.5\n");

        PersonalOptionsStore store = new(_data);
        PersonalOptionsSession session = new()
        {
            VersionPath = _game,
            GameFolder = _game,
            Pid = int.MaxValue,
            StartedUtc = DateTimeOffset.UtcNow,
            Baseline = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fov"] = "70",
                ["mouseSensitivity"] = "0.5"
            },
            Keys = ["fov", "mouseSensitivity"]
        };
        store.WriteSession(session);

        PersonalOptionsFlow flow = new(store);
        int recovered = flow.RecoverExitedSessions();

        Assert.AreEqual(1, recovered, "进程不存在的会话应被补采集。");
        IReadOnlyDictionary<string, string> profile = store.ReadProfile();
        Assert.AreEqual("90", profile["fov"], "只有真正被改过的 fov 应写回档案。");
        Assert.IsFalse(profile.ContainsKey("mouseSensitivity"), "与基线相同的键不应进入档案。");
        Assert.IsFalse(store.SessionExists(session.Id), "补采集完成后应删除会话文件。");
    }

    [TestMethod]
    public void RecoverExitedSessions_KeepsRunningSession()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "fov:90\n");

        using Process current = Process.GetCurrentProcess();
        PersonalOptionsStore store = new(_data);
        PersonalOptionsSession session = new()
        {
            VersionPath = _game,
            GameFolder = _game,
            Pid = current.Id,
            StartedUtc = current.StartTime.ToUniversalTime(),
            Baseline = new Dictionary<string, string>(StringComparer.Ordinal) { ["fov"] = "70" },
            Keys = ["fov"]
        };
        store.WriteSession(session);

        PersonalOptionsFlow flow = new(store);
        Assert.AreEqual(0, flow.RecoverExitedSessions(), "仍在运行的进程不能被当作已退出。");
        Assert.IsTrue(store.SessionExists(session.Id), "运行中的会话文件必须保留。");
        Assert.AreEqual(0, store.ReadProfile().Count, "运行中的会话不应提前写档案。");
        Assert.IsTrue(flow.HasActiveSession(_game), "同一目录存在运行中的会话时 HasActiveSession 应为真。");
    }

    [TestMethod]
    public void Capture_OnlyWritesChangedKeys()
    {
        string path = Path.Combine(_game, "options.txt");
        File.WriteAllText(path, "fov:90\nmouseSensitivity:0.5\n");

        PersonalOptionsStore store = new(_data);
        PersonalOptionsSession session = new()
        {
            VersionPath = _game,
            GameFolder = _game,
            Baseline = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fov"] = "70",
                ["mouseSensitivity"] = "0.5"
            },
            Keys = ["fov", "mouseSensitivity"]
        };

        IReadOnlyCollection<string> changed = new PersonalOptionsFlow(store).Capture(session);

        Assert.AreEqual("fov", string.Join(",", changed), "只应采集 fov。");
        IReadOnlyDictionary<string, string> profile = store.ReadProfile();
        Assert.AreEqual("90", profile["fov"]);
        Assert.IsFalse(profile.ContainsKey("mouseSensitivity"));
    }

    // ─────────────────────────── 数据层 ───────────────────────────

    [TestMethod]
    public void Profile_And_Session_UseCamelCaseJson()
    {
        PersonalOptionsStore store = new(_data);
        store.WriteProfile(new Dictionary<string, string>(StringComparer.Ordinal) { ["fov"] = "90" });
        PersonalOptionsSession tracked = new()
        {
            VersionPath = _game,
            GameFolder = _game,
            Pid = 1234,
            StartedUtc = DateTimeOffset.UnixEpoch,
            Baseline = new Dictionary<string, string>(StringComparer.Ordinal) { ["fov"] = "70" },
            Keys = ["fov"]
        };
        store.WriteSession(tracked);

        string profile = File.ReadAllText(store.ProfilePath);
        Assert.IsTrue(profile.Contains("\"version\": 1", StringComparison.Ordinal), $"档案应包含 version/settings，实际：{profile}");
        Assert.IsTrue(profile.Contains("\"values\"", StringComparison.Ordinal), $"档案应包含 camelCase 的 values，实际：{profile}");
        Assert.IsTrue(profile.Contains("\"fov\"", StringComparison.Ordinal));

        string session = File.ReadAllText(store.SessionPath(tracked.Id));
        Assert.IsTrue(session.Contains("\"versionPath\"", StringComparison.Ordinal), $"会话应使用 camelCase 字段，实际：{session}");
        Assert.IsTrue(session.Contains("\"startedUtc\"", StringComparison.Ordinal));
        Assert.IsFalse(session.Contains("\"tracked\"", StringComparison.Ordinal), "仅在内存中使用的 Tracked 不应写入 JSON。");
    }

    [TestMethod]
    public void ReadOptions_RejectsOversizedFile()
    {
        string path = Path.Combine(_game, "options.txt");
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.SetLength(PersonalOptionsStore.MaxOptionsBytes + 1);

        PersonalOptionsStore store = new(_data);
        bool threw = false;
        try
        {
            store.ReadOptions(_game);
        }
        catch (IOException error)
        {
            threw = true;
            Assert.IsTrue(error.Message.Contains("options.txt", StringComparison.Ordinal),
                $"异常消息应提到 options.txt，实际为：{error.Message}");
        }
        Assert.IsTrue(threw, "超过 4 MiB 的 options.txt 必须被拒绝。");
    }

    [TestMethod]
    public void Profile_RoundTripsValuesAndNormalizesKeys()
    {
        PersonalOptionsStore store = new(_data);
        store.WriteProfile(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key_key.swapHands"] = "key.keyboard.g",
            ["mouseSensitivity"] = "0.35"
        });

        IReadOnlyDictionary<string, string> profile = store.ReadProfile();
        Assert.AreEqual("key.keyboard.g", profile["key_key.swapOffhand"], "swapHands 应归一为 swapOffhand 存储。");
        Assert.AreEqual("0.35", profile["mouseSensitivity"]);
    }

    /// <summary>
    /// 在读取 <c>options.txt</c> 之后立刻追加一行，模拟游戏或外部程序在准备阶段改写文件。
    /// </summary>
    private sealed class TamperingStore(string dataDirectory) : PersonalOptionsStore(dataDirectory)
    {
        /// <inheritdoc />
        public override PersonalOptionsSnapshot? ReadOptions(string gameFolder)
        {
            PersonalOptionsSnapshot? snapshot = base.ReadOptions(gameFolder);
            if (snapshot is not null)
                File.AppendAllText(OptionsPath(gameFolder), "achievements:1\n");
            return snapshot;
        }
    }
}
