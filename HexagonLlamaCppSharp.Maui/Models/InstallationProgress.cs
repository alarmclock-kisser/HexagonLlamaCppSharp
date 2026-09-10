namespace HexagonLlamaCppSharp.Maui.Models;

public sealed record InstallationProgress(int Percent, string Phase, string Message);

public enum ProcessLogSeverity
{
    Information,
    Warning,
    Error,
    StandardError
}

public sealed record ProcessLogLine(string Text, ProcessLogSeverity Severity)
{
    public ProcessLogLine(string text, bool isError)
        : this(text, isError ? ProcessLogSeverity.Error : ProcessLogSeverity.Information)
    {
    }

    public bool IsError => Severity == ProcessLogSeverity.Error;

    public bool IsWarning => Severity == ProcessLogSeverity.Warning;

    public bool IsStandardError => Severity == ProcessLogSeverity.StandardError;
}

public static class ProcessLogSeverityClassifier
{
    public static ProcessLogSeverity Classify(string text, bool isStandardError)
    {
        if (!isStandardError)
        {
            return ProcessLogSeverity.Information;
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(token => string.Equals(token, "W", StringComparison.OrdinalIgnoreCase)) ||
            ContainsWord(text, "warning"))
        {
            return ProcessLogSeverity.Warning;
        }

        if (tokens.Any(token => string.Equals(token, "E", StringComparison.OrdinalIgnoreCase)) ||
            ContainsWord(text, "error") ||
            ContainsWord(text, "fatal") ||
            ContainsWord(text, "failed"))
        {
            return ProcessLogSeverity.Error;
        }

        if (tokens.Any(token => string.Equals(token, "I", StringComparison.OrdinalIgnoreCase)) ||
            ContainsWord(text, "info"))
        {
            return ProcessLogSeverity.Information;
        }

        return ProcessLogSeverity.StandardError;
    }

    private static bool ContainsWord(string text, string word)
    {
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(word, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var startsAtWordBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var endIndex = index + word.Length;
            var endsAtWordBoundary = endIndex == text.Length || !char.IsLetterOrDigit(text[endIndex]);
            if (startsAtWordBoundary && endsAtWordBoundary)
            {
                return true;
            }

            startIndex = endIndex;
        }

        return false;
    }
}

public sealed record ExecutionProbeResult(bool Succeeded, string Message);

public sealed record ExtractedNativeAsset(string FileName, string Path, long Length, string Sha256);
