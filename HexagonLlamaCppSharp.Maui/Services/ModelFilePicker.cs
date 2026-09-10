using Android.Provider;
using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class ModelFilePicker : IModelFilePicker
{
    public async Task<ModelFile?> PickAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "GGUF-Modell auswählen",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = ["application/octet-stream", "application/x-gguf", "*/*"]
            })
        });

        if (result is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.FullPath))
        {
            throw new IOException("Der ausgewählte Speicherort liefert keinen direkt zugreifbaren Dateipfad.");
        }

        var path = ResolvePath(result.FullPath);
        var appDataDirectory = Path.GetFullPath(FileSystem.AppDataDirectory) + Path.DirectorySeparatorChar;
        var cacheDirectory = Path.GetFullPath(FileSystem.CacheDirectory) + Path.DirectorySeparatorChar;
        if (path.StartsWith(appDataDirectory, StringComparison.Ordinal) ||
            path.StartsWith(cacheDirectory, StringComparison.Ordinal))
        {
            throw new IOException("Das Modell darf nicht aus einem App-Cache oder einer AppData-Kopie geladen werden.");
        }

        if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Bitte eine GGUF-Datei auswählen.");
        }

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Die ausgewählte GGUF-Datei ist nicht mehr verfügbar.", path);
        }

        return new ModelFile(path, fileInfo.Name, fileInfo.Length);
    }

    private static string ResolvePath(string selectedPath)
    {
        if (!Uri.TryCreate(selectedPath, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            return Path.GetFullPath(selectedPath);
        }

        if (uri.IsFile)
        {
            return Path.GetFullPath(uri.LocalPath);
        }

        if (!string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Der ausgewählte Speicherort liefert keinen lokalen Dateipfad.");
        }

        var contentUri = global::Android.Net.Uri.Parse(selectedPath)
            ?? throw new IOException("Die ausgewählte Content-URI ist ungültig.");
        var resolver = global::Android.App.Application.Context.ContentResolver
            ?? throw new IOException("Android konnte den ausgewählten Speicheranbieter nicht öffnen.");
        using var cursor = resolver.Query(
            contentUri,
            [MediaStore.Files.FileColumns.Data],
            null,
            null,
            null);
        if (cursor is null || !cursor.MoveToFirst())
        {
            throw new IOException("Der ausgewählte Speicheranbieter stellt keinen direkten Dateipfad bereit. Eine Kopie des Modells wird nicht angelegt.");
        }

        var dataColumn = cursor.GetColumnIndex(MediaStore.Files.FileColumns.Data);
        if (dataColumn < 0 || cursor.IsNull(dataColumn))
        {
            throw new IOException("Der ausgewählte Speicheranbieter stellt keinen direkten Dateipfad bereit. Eine Kopie des Modells wird nicht angelegt.");
        }

        var path = cursor.GetString(dataColumn);
        return string.IsNullOrWhiteSpace(path)
            ? throw new IOException("Der ausgewählte Speicheranbieter stellt keinen direkten Dateipfad bereit. Eine Kopie des Modells wird nicht angelegt.")
            : Path.GetFullPath(path);
    }
}