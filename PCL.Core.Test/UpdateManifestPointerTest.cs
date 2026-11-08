using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Updates;
using System;

namespace PCL.Core.Test;

/// <summary>
/// 更新清单指针的解析测试。
/// <para>
/// 重点是那几个「实战翻车」的用例：反代返回的 <c>404: Not Found</c>、
/// 被墙时回的 HTML 错误页、以及内容为空但 HTTP 200 的情况——
/// 它们都必须是「这条通道失败」，而不是「清单写错了」。
/// </para>
/// </summary>
[TestClass]
public class UpdateManifestPointerTest
{
    [TestMethod]
    public void ExtractShouldReadSingleLineJson()
    {
        Assert.AreEqual(
            "x1B1C1/PCL_Plus_OTA/releases/latest",
            UpdateManifestPointer.Extract("{\"update\": \"x1B1C1/PCL_Plus_OTA/releases/latest\"}"));
    }

    [TestMethod]
    public void ExtractShouldReadBareJsonString()
    {
        Assert.AreEqual(
            "x1B1C1/PCL_Plus_OTA/releases/latest",
            UpdateManifestPointer.Extract("\"x1B1C1/PCL_Plus_OTA/releases/latest\""));
    }

    [TestMethod]
    public void ExtractShouldReadPlainText()
    {
        Assert.AreEqual(
            "x1B1C1/PCL_Plus_OTA/releases/latest",
            UpdateManifestPointer.Extract("x1B1C1/PCL_Plus_OTA/releases/latest"));
    }

    [TestMethod]
    public void ExtractShouldReadShortForm()
    {
        Assert.AreEqual("x1B1C1/PCL_Plus_OTA", UpdateManifestPointer.Extract("x1B1C1/PCL_Plus_OTA"));
    }

    [TestMethod]
    public void ExtractShouldTolerateBomAndNewline()
    {
        Assert.AreEqual(
            "x1B1C1/PCL_Plus_OTA/releases/latest",
            UpdateManifestPointer.Extract("\uFEFF{\"update\":\"x1B1C1/PCL_Plus_OTA/releases/latest\"}\n"));
    }

    [TestMethod]
    public void ExtractShouldFallBackToSoleStringValue()
    {
        Assert.AreEqual(
            "x1B1C1/PCL_Plus_OTA",
            UpdateManifestPointer.Extract("{\"whatever\": \"x1B1C1/PCL_Plus_OTA\"}"));
    }

    /// <summary>实战 bug：反代 404 的响应体被当成了指针。</summary>
    [TestMethod]
    public void ExtractShouldRejectNotFoundBody()
    {
        var ex = Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("404: Not Found"));
        Assert.IsTrue(ex.Message.Contains("不是仓库指针"), ex.Message);
    }

    [TestMethod]
    public void ExtractShouldRejectHtmlErrorPage()
    {
        Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestPointer.Extract("<!DOCTYPE html><html><body>502 Bad Gateway</body></html>"));
    }

    [TestMethod]
    public void ExtractShouldRejectEmptyBody()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("   \n\t "));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract(null));
    }

    [TestMethod]
    public void ExtractShouldRejectJsonWithoutPointer()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("{\"update\": \"\"}"));
        Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestPointer.Extract("{\"a\": \"x1B1C1/PCL_Plus_OTA\", \"b\": \"x1B1C1/PCL_Plus_OTA-service\"}"));
    }

    [TestMethod]
    public void ExtractShouldRejectBrokenJson()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("{\"update\": "));
    }

    [TestMethod]
    public void LooksLikePointerShouldAcceptOwnerRepoForms()
    {
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("x1B1C1/PCL_Plus_OTA"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("x1B1C1/PCL_Plus_OTA/releases/latest"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("/x1B1C1/PCL_Plus_OTA/"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("https://github.com/x1B1C1/PCL_Plus_OTA"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("github.com/x1B1C1/PCL_Plus_OTA"));
    }

    [TestMethod]
    public void LooksLikePointerShouldRejectGarbage()
    {
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(null));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(""));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("404: Not Found"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Not Found"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Pcl-Lite"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Pcl-Lite / Pcl-lite"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("<html>"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("{\"update\":\"x1B1C1/PCL_Plus_OTA\"}"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(new string('a', 300) + "/b"));
    }

    [TestMethod]
    public void ParseShouldSplitOwnerAndRepo()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("x1B1C1/PCL_Plus_OTA/releases/latest");
        Assert.AreEqual("x1B1C1", owner);
        Assert.AreEqual("PCL_Plus_OTA", repo);
    }

    [TestMethod]
    public void ParseShouldHandleFullUrl()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("https://github.com/x1B1C1/PCL_Plus_OTA/releases/latest");
        Assert.AreEqual("x1B1C1", owner);
        Assert.AreEqual("PCL_Plus_OTA", repo);
    }

    [TestMethod]
    public void ParseShouldStripGitSuffixAndQuotes()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("\"https://github.com/x1B1C1/PCL_Plus_OTA.git\"");
        Assert.AreEqual("x1B1C1", owner);
        Assert.AreEqual("PCL_Plus_OTA", repo);
    }

    [TestMethod]
    public void ParseShouldRejectNonPointer()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Parse("404: Not Found"));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Parse(""));
    }

    /// <summary>
    /// <c>self</c> 必须展开成「这个 exe 自己认定的仓库」。
    /// <para>
    /// 这条是「复刻仓库不再需要改 update.json」的地基：所有仓库里的 json 内容都一样，
    /// 具体地址由代码里的常量决定，所以合并永远不会在这份文件上冲突。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ExtractShouldResolveSelfKeyword()
    {
        var expected = $"{ProjectLinks.ServiceOwner}/{ProjectLinks.ServiceRepo}/releases/latest";
        Assert.AreEqual(expected, UpdateManifestPointer.Extract("{\"update\": \"self\"}"));
        Assert.AreEqual(expected, UpdateManifestPointer.Extract("\"self\""));
        Assert.AreEqual(expected, UpdateManifestPointer.Extract("SELF"));
    }

    [TestMethod]
    public void LooksLikePointerShouldAcceptSelf()
    {
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("self"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer(" Self "));
    }

    [TestMethod]
    public void ParseShouldResolveSelf()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("self");
        Assert.AreEqual(ProjectLinks.ServiceOwner, owner);
        Assert.AreEqual(ProjectLinks.ServiceRepo, repo);
    }

    [TestMethod]
    public void ResolveSelfShouldLeaveOtherPointersAlone()
    {
        Assert.AreEqual("someone/fork/releases/latest",
            UpdateManifestPointer.ResolveSelf("someone/fork/releases/latest"));
        Assert.AreEqual(string.Empty, UpdateManifestPointer.ResolveSelf(null));
    }
}
