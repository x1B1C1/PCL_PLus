namespace PCL.Modules.Minecraft.CrashAnalysis;

internal sealed class CrashAnalysisContext(
    int processId,
    string tempFolder)
{
    public int ProcessId { get; } = processId;

    public string TempFolder { get; } = tempFolder;

    public List<CrashLogEntry> RawFiles { get; } = [];

    public List<string> OutputFiles { get; } = [];

    public McInstance? Instance { get; set; }

    public CrashLogEntry? DirectOpenFile { get; set; }

    public CrashLogSet? PreparedLogs { get; set; }

    public CrashAnalysisResult? Result { get; set; }

    /// <summary>与最近一次健康环境快照相比的差异；没有基线或对比失败时为空表。</summary>
    public IReadOnlyList<PCL.Core.Minecraft.EnvironmentSnapshot.EnvironmentChange> EnvironmentChanges { get; set; } = [];

    public string LogAll { get; set; } = string.Empty;
}