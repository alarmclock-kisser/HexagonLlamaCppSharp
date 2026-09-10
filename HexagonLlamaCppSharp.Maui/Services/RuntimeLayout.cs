namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class RuntimeLayout
{
    public RuntimeLayout(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        RootDirectory = Path.Combine(appDataDirectory, "llama-runtime");
        ToolchainDirectory = Path.Combine(RootDirectory, "toolchain");
        SourceDirectory = Path.Combine(RootDirectory, "source");
        BuildDirectory = Path.Combine(RootDirectory, "build");
        NativeLibrariesDirectory = Path.Combine(RootDirectory, "native-libraries");
        ProbeDirectory = Path.Combine(RootDirectory, "probe");
        ToolchainStatePath = Path.Combine(RootDirectory, "toolchain-installation.json");
        ManifestPath = Path.Combine(RootDirectory, "installation.json");
    }

    public string RootDirectory { get; }

    public string ToolchainDirectory { get; }

    public string SourceDirectory { get; }

    public string BuildDirectory { get; }

    public string NativeLibrariesDirectory { get; }

    public string ProbeDirectory { get; }

    public string ToolchainStatePath { get; }

    public string ManifestPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ToolchainDirectory);
        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(BuildDirectory);
        Directory.CreateDirectory(NativeLibrariesDirectory);
        Directory.CreateDirectory(ProbeDirectory);
    }
}
