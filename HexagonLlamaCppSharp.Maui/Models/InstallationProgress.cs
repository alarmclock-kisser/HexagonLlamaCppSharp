namespace HexagonLlamaCppSharp.Maui.Models;

public sealed record InstallationProgress(int Percent, string Phase, string Message);

public sealed record ProcessLogLine(string Text, bool IsError);

public sealed record ExecutionProbeResult(bool Succeeded, string Message);

public sealed record ExtractedNativeAsset(string FileName, string Path, long Length, string Sha256);
