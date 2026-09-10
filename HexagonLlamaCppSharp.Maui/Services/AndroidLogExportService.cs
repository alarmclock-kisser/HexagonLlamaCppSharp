using System.Text;
using Android.Content;
using Android.Provider;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class AndroidLogExportService : ILogExportService
{
    private const string DownloadRelativePath = "Download/HexagonLlamaCppSharp/";

    public async Task<string> SaveAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var fileName = $"hexagon-installation-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        cancellationToken.ThrowIfCancellationRequested();
        if (global::Android.OS.Environment.IsExternalStorageManager)
        {
            return await SaveDirectlyAsync(fileName, content, cancellationToken);
        }

        return await SaveWithMediaStoreAsync(fileName, content, cancellationToken);
    }

    private static async Task<string> SaveDirectlyAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var externalRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
            ?? throw new IOException("Android hat keinen öffentlichen Speicherpfad bereitgestellt.");
        var directory = Path.Combine(externalRoot, "Download", "HexagonLlamaCppSharp");
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(directory, fileName);
        var temporaryPath = destinationPath + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return destinationPath;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<string> SaveWithMediaStoreAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var resolver = global::Android.App.Application.Context.ContentResolver
            ?? throw new IOException("Android konnte den Downloads-Speicheranbieter nicht öffnen.");
        using var values = new ContentValues();
        values.Put("_display_name", fileName);
        values.Put("mime_type", "text/plain");
        values.Put("relative_path", DownloadRelativePath);
        values.Put("is_pending", 1);
        using var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new IOException("Android konnte die Logdatei in Downloads nicht anlegen.");
        try
        {
            await WriteContentAsync(resolver, uri, content, cancellationToken);
            using var completedValues = new ContentValues();
            completedValues.Put("is_pending", 0);
            if (resolver.Update(uri, completedValues, null, null) != 1)
            {
                throw new IOException("Android konnte die Logdatei in Downloads nicht veröffentlichen.");
            }

            return $"{DownloadRelativePath}{fileName}";
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    private static async Task WriteContentAsync(
        ContentResolver resolver,
        global::Android.Net.Uri uri,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = resolver.OpenOutputStream(uri)
            ?? throw new IOException("Android konnte die Logdatei in Downloads nicht öffnen.");
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}