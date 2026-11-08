namespace PCL.Modules.Minecraft.CrashAnalysis.Logs;

internal sealed class CrashLogText
{
    public required CrashLogKind Kind { get; init; }

    public required string Text { get; init; }

    public string? FilePath { get; init; }
}