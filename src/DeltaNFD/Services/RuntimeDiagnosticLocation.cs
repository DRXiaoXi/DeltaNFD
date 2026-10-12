namespace DeltaNFD.Services;

internal static class RuntimeDiagnosticLocation
{
    internal static string Resolve(OperationResult result, string logRoot)
    {
        var root = Path.GetFullPath(logRoot);
        var candidate = result.DiagnosticsDirectory;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            // Compatibility with older backend results; new results carry a structured directory.
            var match = System.Text.RegularExpressions.Regex.Match(result.Message, @"(?:^|\n)诊断目录[：:]\s*([^\r\n]+)");
            if (match.Success) candidate = match.Groups[1].Value.Trim();
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.StartsWith(@"\\", StringComparison.Ordinal)
                && Path.IsPathFullyQualified(candidate))
            {
                var full = Path.GetFullPath(candidate);
                if ((full.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    && Directory.Exists(full)) return full;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { }
        return root;
    }
}
