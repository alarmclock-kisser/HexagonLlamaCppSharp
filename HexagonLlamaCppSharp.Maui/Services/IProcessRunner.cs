using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessSpec specification,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);
}

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

public sealed record ProcessResult(int ExitCode, TimeSpan Duration);

public sealed class ProcessExecutionException : Exception
{
    public ProcessExecutionException(string fileName, Exception innerException)
        : base($"Unable to start process '{fileName}': {innerException.Message}", innerException)
    {
        FileName = fileName;
    }

    public string FileName { get; }
}
