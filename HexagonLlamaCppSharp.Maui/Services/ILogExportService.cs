namespace HexagonLlamaCppSharp.Maui.Services;

public interface ILogExportService
{
    Task<string> SaveAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken,
        string fileNamePrefix = "hexagon-installation");
}