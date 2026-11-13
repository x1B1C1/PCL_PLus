using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class PlusLiquidGlassConfigurationTests
{
    private static Dictionary<string, CustomAttributeData> Items(Type group) =>
        group.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(property => property.CustomAttributes)
            .Where(attribute => attribute.AttributeType.IsGenericType
                && attribute.AttributeType.GetGenericTypeDefinition() == typeof(ConfigItemAttribute<>))
            .ToDictionary(attribute => (string)attribute.ConstructorArguments[0].Value!);

    [TestMethod]
    public void NewMaterialCannotReadOrOverwriteLegacyOpticalValues()
    {
        Type preference = typeof(Config).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(type => type.Name == "PreferenceConfigGroup");
        Type glass = preference.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(type => type.Name == "TahoeGlassConfigGroup");
        Type theme = preference.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(type => type.Name == "ThemeConfigGroup");
        var newItems = Items(glass);
        var oldItems = Items(theme);

        Assert.AreEqual(21, newItems.Count);
        Assert.IsTrue(newItems.Keys.All(key => key.StartsWith("UiTahoeGlass", StringComparison.Ordinal)));
        Assert.IsFalse(newItems.Keys.Intersect(oldItems.Keys).Any());
        Assert.AreEqual(70, oldItems["UiLiquidGlassRefraction"].ConstructorArguments[1].Value);
        Assert.AreEqual(72, oldItems["UiLiquidGlassHighlight"].ConstructorArguments[1].Value);
        Assert.AreEqual(35, oldItems["UiLiquidGlassDispersion"].ConstructorArguments[1].Value);

        var expected = new Dictionary<string, object>
        {
            ["Enabled"] = false, ["DesktopBackdrop"] = false, ["Preset"] = 1,
            ["FrameRate"] = 60, ["Frost"] = 8, ["Refraction"] = 5,
            ["RefractionPercent"] = -1, ["SamplingFrameRate"] = -1,
            ["SamplingQuality"] = 100, ["BlurRadius"] = -1, ["Dispersion"] = -1,
            ["Highlight"] = -1, ["Diffuse"] = -1, ["EdgeSharpness"] = -1,
            ["TintScale"] = 100, ["CardOpacity"] = -1, ["Brightness"] = 0,
            ["CardBrightness"] = 0, ["Motion"] = -1, ["Consent"] = false,
            ["DesktopConsent"] = false
        };
        foreach (var entry in expected)
        {
            var attribute = newItems["UiTahoeGlass" + entry.Key];
            Assert.AreEqual(entry.Value, attribute.ConstructorArguments[1].Value, entry.Key);
            Assert.AreEqual((int)ConfigSource.Local, attribute.ConstructorArguments[2].Value, entry.Key);
        }
    }

    [TestMethod]
    public void LegacyMaximumRefractionDoesNotBecomeTheNewDefault()
    {
        Assert.AreEqual(100, LiquidGlassMaterial.ResolveRefractionPercent(-1, 5));
        Assert.AreEqual(.05, LiquidGlassMaterial.RefractionFromPercent(100), 1e-9);
        Assert.IsFalse(LiquidGlassPerformance.ShouldCaptureDesktop(false, true, true, true));
        Assert.IsFalse(LiquidGlassPerformance.ShouldCaptureDesktop(true, true, true, false));
    }
}
