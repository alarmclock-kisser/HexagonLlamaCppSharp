namespace HexagonLlamaCppSharp.Maui.Services;

public static class AndroidNativeToolchain
{
    private static readonly IReadOnlyDictionary<string, string> NativeFileNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cmake"] = "libcmake.so",
            ["ninja"] = "libninja.so",
            ["clang"] = "libclang.so",
            ["clang++"] = "libclangxx.so",
            ["ld.lld"] = "libld.lld.so",
            ["llvm-ar"] = "libllvm-ar.so",
            ["llvm-ranlib"] = "libllvm-ranlib.so",
            ["llvm-objcopy"] = "libllvm-objcopy.so"
        };

    public static bool TryResolve(
        out IReadOnlyDictionary<string, string> executables,
        out string message)
    {
        executables = new Dictionary<string, string>(StringComparer.Ordinal);
        message = string.Empty;

        if (!OperatingSystem.IsAndroid())
        {
            message = "Die Android-Native-Toolchain ist nur auf Android verfügbar.";
            return false;
        }

        var nativeLibraryDirectory = GetNativeLibraryDirectory();
        if (string.IsNullOrWhiteSpace(nativeLibraryDirectory))
        {
            message = "Das Android-Native-Library-Verzeichnis konnte nicht ermittelt werden.";
            return false;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var item in NativeFileNames)
        {
            var path = Path.Combine(nativeLibraryDirectory, item.Value);
            if (File.Exists(path))
            {
                resolved[item.Key] = path;
            }
            else
            {
                missing.Add(item.Value);
            }
        }

        if (missing.Count > 0)
        {
            message = $"APK-Native-Toolchain unvollständig; fehlend: {string.Join(", ", missing)}.";
            return false;
        }

        executables = resolved;
        message = $"APK-Native-Toolchain aus {nativeLibraryDirectory} geladen.";
        return true;
    }

    public static string? GetNativeLibraryDirectory()
    {
        if (!OperatingSystem.IsAndroid())
        {
            return null;
        }

        return Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
    }

    public static bool IsNativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var nativeLibraryDirectory = GetNativeLibraryDirectory();
        if (string.IsNullOrWhiteSpace(nativeLibraryDirectory))
        {
            return false;
        }

        var normalizedDirectory = Path.GetFullPath(nativeLibraryDirectory) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.StartsWith(normalizedDirectory, StringComparison.Ordinal) && File.Exists(normalizedPath))
        {
            return true;
        }

        if (!File.Exists(normalizedPath))
        {
            return false;
        }

        var resolvedTarget = new FileInfo(normalizedPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        return resolvedTarget is not null &&
            Path.GetFullPath(resolvedTarget).StartsWith(normalizedDirectory, StringComparison.Ordinal);
    }
}