using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IToolchainArchiveTransferService
{
    Task<bool> ImportFromDownloadsAsync(
        ToolchainManifest manifest,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);

    Task<bool> ExportToDownloadsAsync(
        ToolchainManifest manifest,
        IProgress<InstallationProgress> progress,
        IProgress<ProcessLogLine> output,
        CancellationToken cancellationToken);

    Task<bool> HasCompleteAppArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken);

    Task<bool> HasAnyDownloadArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken);

    Task<bool> HasCompleteDownloadArchivesAsync(
        ToolchainManifest manifest,
        CancellationToken cancellationToken);
}