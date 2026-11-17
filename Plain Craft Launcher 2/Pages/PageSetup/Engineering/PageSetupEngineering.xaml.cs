#if BETA_A
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Controls;
using PCL.Core.App;
using PCL.Modules.Base;
using PCL.Modules.UI.Theme;

namespace PCL.Pages.PageSetup;

/// <summary>
/// Beta A 工程调试页：只用来对比玻璃层数与参数对观感、GPU 占用的影响。
/// 正式构建里这个文件不参与编译（csproj 的 EngineeringExclude）。
/// </summary>
public partial class PageSetupEngineering : MyPageRight
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<double> _frameTimes = new();
    private long _lastFrameTicks;
    private bool _loaded;

    public PageSetupEngineering()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            SyncFromConfig();
            CompositionTarget.Rendering += OnRendering;
            _timer.Tick += (_, _) => RefreshMetrics();
            _timer.Start();
            RefreshMetrics();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            CompositionTarget.Rendering -= OnRendering;
            EngineeringLayers.Revert();
        };
    }

    private void SyncFromConfig()
    {
        CheckGlass.SetChecked(Config.Preference.TahoeGlass.IsEnabled, false);
        SliderOpacity.Value = Math.Clamp(Config.Preference.Theme.WindowOpacity, 0, 100);
        SliderFrost.Value = Math.Clamp(Config.Preference.TahoeGlass.FrostRadius, 0, 24);
        SliderRefraction.Value = Math.Clamp(Config.Preference.TahoeGlass.RefractionStrength, 0, 15);
    }

    private void CheckGlass_Change(object senderRaw, bool user)
    {
        var enabled = ((MyCheckBox)senderRaw).Checked == true;
        Config.Preference.TahoeGlass.IsEnabled = enabled;
        Config.Preference.TahoeGlass.PerformanceConsent = enabled;
        LiquidGlassCore.Refresh();
        RefreshMetrics();
    }

    private void BtnLayer_Click(object sender, MouseButtonEventArgs e)
    {
        int mode = int.TryParse(((MyButton)sender).Tag?.ToString(), out int parsed) ? parsed : 0;
        switch (mode)
        {
            case 0:
                EngineeringLayers.Revert();
                LiquidGlassCore.ApplyOpacity(100);
                LabHint.Text = "L1：只有主窗口一层玻璃，卡片不透明。";
                break;
            case 1:
                EngineeringLayers.Revert();
                LiquidGlassCore.ApplyOpacity(80);
                LabHint.Text = "L2：卡片用半透明色，没有额外模糊。这是推荐形态。";
                break;
            default:
                EngineeringLayers.Apply(2);
                LabHint.Text = "L3：每张卡片底板再叠一层模糊，用来复现多层玻璃。";
                break;
        }
        RefreshMetrics();
    }

    private void Slider_Change(object senderRaw, bool user)
    {
        if (senderRaw == SliderOpacity)
        {
            LiquidGlassCore.ApplyOpacity(SliderOpacity.Value);
        }
        else if (senderRaw == SliderFrost)
        {
            Config.Preference.TahoeGlass.FrostRadius = SliderFrost.Value;
            LiquidGlassCore.Refresh();
        }
        else if (senderRaw == SliderRefraction)
        {
            Config.Preference.TahoeGlass.RefractionStrength = Math.Max(5, SliderRefraction.Value);
            LiquidGlassCore.Refresh();
        }
    }

    private void BtnReset_Click(object sender, MouseButtonEventArgs e)
    {
        EngineeringLayers.Revert();
        Config.Preference.Theme.WindowOpacity = 100;
        Config.Preference.TahoeGlass.FrostRadius = 6;
        Config.Preference.TahoeGlass.RefractionStrength = 5;
        SyncFromConfig();
        LiquidGlassCore.Refresh();
        LabHint.Text = string.Empty;
        RefreshMetrics();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            double ms = (now - _lastFrameTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            if (ms is > 0 and < 1000)
            {
                _frameTimes.Enqueue(ms);
                while (_frameTimes.Count > 120) _frameTimes.Dequeue();
            }
        }
        _lastFrameTicks = now;
    }

    private void RefreshMetrics()
    {
        double? gpu = GpuUsageMonitor.Sample();
        LabGpu.Text = gpu is { } value ? $"{value:0}%" : "--";
        LabSurfaces.Text = $"玻璃面 {EngineeringLayers.CountGlassSurfaces()}";

        if (_frameTimes.Count > 0)
        {
            double average = _frameTimes.Average();
            LabFrame.Text = $"帧时间 {average:0.0} ms（约 {1000d / Math.Max(average, 0.1):0} FPS）";
        }
    }

    private async void BtnRunCompare_Click(object sender, MouseButtonEventArgs e)
    {
        BtnRunCompare.IsEnabled = false;
        var results = new List<string>();
        try
        {
            foreach ((string label, int mode) in new[] { ("L1", 0), ("L2", 1), ("L3", 2) })
            {
                ApplyModeForCompare(mode);
                _frameTimes.Clear();
                await Task.Delay(1500);
                var gpuSamples = new List<double>();
                for (int index = 0; index < 4; index++)
                {
                    await Task.Delay(500);
                    if (GpuUsageMonitor.Sample() is { } sample) gpuSamples.Add(sample);
                }
                double avgFrame = _frameTimes.Count > 0 ? _frameTimes.Average() : 0d;
                string gpuText = gpuSamples.Count > 0 ? $"{gpuSamples.Average():0}%" : "--";
                results.Add($"{label}：GPU {gpuText} · 帧时间 {avgFrame:0.0} ms");
            }
        }
        finally
        {
            EngineeringLayers.Revert();
            BtnRunCompare.IsEnabled = true;
        }

        LabHint.Text = string.Join("　|　", results);
    }

    private void ApplyModeForCompare(int mode)
    {
        switch (mode)
        {
            case 0:
                EngineeringLayers.Revert();
                LiquidGlassCore.ApplyOpacity(100);
                break;
            case 1:
                EngineeringLayers.Revert();
                LiquidGlassCore.ApplyOpacity(80);
                break;
            default:
                EngineeringLayers.Apply(2);
                break;
        }
    }
}
#endif
