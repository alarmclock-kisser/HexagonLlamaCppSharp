namespace HexagonLlamaCppSharp.Maui.Services;

public interface ISharedStorageAccessService
{
    Task<bool> RequestAccessAsync(CancellationToken cancellationToken);
}
