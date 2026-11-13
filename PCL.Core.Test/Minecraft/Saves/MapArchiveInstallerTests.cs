using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Saves;

namespace PCL.Core.Test.Minecraft.Saves;

[TestClass]
public class MapArchiveInstallerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-maptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // 清理失败不影响测试结论。
        }
    }

    [TestMethod]
    public void DownloadFolderShouldSitUnderSaves()
    {
        var saves = Path.Combine(_root, "saves");
        var folder = MapArchiveInstaller.GetDownloadFolder(saves);
        Assert.AreEqual(Path.Combine(saves, MapArchiveInstaller.DownloadFolderName), folder);
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public void InstallShouldExtractSingleRoot()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        var archive = CreateArchive("MyWorld", ("MyWorld/level.dat", "data"), ("MyWorld/region/r.0.0.mca", "region"));

        var target = MapArchiveInstaller.Install(archive, saves);

        Assert.AreEqual(Path.Combine(saves, "MyWorld"), target);
        Assert.IsTrue(File.Exists(Path.Combine(target, "level.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "region", "r.0.0.mca")));
        Assert.IsFalse(File.Exists(archive));
    }

    [TestMethod]
    public void InstallShouldNotOverwriteExistingWorld()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(Path.Combine(saves, "MyWorld"));
        File.WriteAllText(Path.Combine(saves, "MyWorld", "level.dat"), "old");
        var archive = CreateArchive("MyWorld", ("MyWorld/level.dat", "new"));

        var target = MapArchiveInstaller.Install(archive, saves);

        Assert.AreEqual(Path.Combine(saves, "MyWorld (2)"), target);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(saves, "MyWorld", "level.dat")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(target, "level.dat")));
    }

    [TestMethod]
    public void InstallShouldRejectMultipleRoots()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        var archive = CreateArchive("bad", ("A/level.dat", "a"), ("B/level.dat", "b"));

        bool threw;
        try
        {
            MapArchiveInstaller.Install(archive, saves);
            threw = false;
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "多顶层目录应当被拒绝。");
        Assert.IsFalse(Directory.Exists(Path.Combine(saves, "A")), "失败时不应留下部分内容。");
        Assert.IsTrue(File.Exists(archive), "失败时原压缩包应当保留。");
    }

    [TestMethod]
    public void InstallShouldRejectPathTraversal()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        var archive = CreateArchive("evil", ("Root/level.dat", "a"), ("Root/../../escape.txt", "x"));

        var threw = false;
        try
        {
            MapArchiveInstaller.Install(archive, saves);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "越界路径应当被拒绝。");
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    private string CreateArchive(string fileName, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_root, fileName + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var item = archive.CreateEntry(entry);
            using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }
}
