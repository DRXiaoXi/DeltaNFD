namespace DeltaNFD.Services;

/// <summary>Resolve the single Delta process rule target without querying or starting a process.</summary>
internal static class DeltaProcessRules
{
    internal const string ExecutableName = DeltaForceLocator.GameProcessName + ".exe";

    internal static string NormalizeTarget(string path)
    {
        path = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择三角洲主进程 " + ExecutableName + " 的完整路径。");
        return Path.GetFullPath(path);
    }

    internal static string FindTarget(IEnumerable<string> roots, IEnumerable<ProcmanRuleEntry> saved, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var root in roots)
        foreach (var path in new[] { Path.Combine(root, "Binaries", "Win64", ExecutableName), Path.Combine(root, "DeltaForce", "Binaries", "Win64", ExecutableName) })
            if (exists(path)) return NormalizeTarget(path);
        foreach (var rule in saved)
        {
            try { return NormalizeTarget(rule.ExecutablePath); }
            catch (ArgumentException) { }
        }
        return "";
    }
}
