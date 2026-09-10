namespace HexagonLlamaCppSharp.Maui.Models;

public enum TermuxStoreSource
{
    PlayStore,
    Fdroid
}

public sealed record TermuxFallbackResult(
    bool Succeeded,
    bool TermuxInstalled,
    string Message);
