using System.Diagnostics;

namespace DeltaNFD.Services;

/// <summary>A missing, named member of the selected official baseline; unreadable files are not missing files.</summary>
internal sealed class Vc14DllMissing(string architecture, string file, string path)
    : IOException($"{architecture} 基线 DLL 缺失：{path}，不能报告修复成功。")
{
    internal string Architecture { get; } = architecture;
    internal string File { get; } = file;
}

/// <summary>Read-only postcheck shared by reinstall and its MSI file-repair fallback.</summary>
internal static class Vc14InstalledFileVerifier
{
    internal static bool IsBaselineFile(Vc14Baseline baseline, string arch, string file) =>
        Vc14ForceReplace.PayloadSet(baseline).Files.Any(p => p.Architecture == arch
            && p.File.Equals(file, StringComparison.OrdinalIgnoreCase));

    internal static void Verify(Vc14Baseline baseline, string arch, string systemDirectory)
    {
        if (arch is not ("x64" or "x86")) throw new IOException("DLL校验架构无效。");
        if (!Path.IsPathFullyQualified(systemDirectory)) throw new IOException("无法确定 DLL 复检的系统目录。");
        var info = Vc14BaselineInfo.For(baseline);
        var expected = Version.Parse(info.Version);
        // Reuse the pinned official names, without invoking replacement or unpacking media.
        foreach (var item in Vc14ForceReplace.PayloadSet(baseline).Files.Where(p => p.Architecture == arch))
        {
            var file = Path.Combine(systemDirectory, item.File);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch (FileNotFoundException) { throw new Vc14DllMissing(arch, item.File, file); }
            catch (DirectoryNotFoundException) { throw new Vc14DllMissing(arch, item.File, file); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("DLL 复检目标不是普通文件：" + file);
            var version = FileVersionInfo.GetVersionInfo(file);
            if (version.FileVersion is null) throw new IOException("DLL版本无法读取：" + file);
            if (version.FileMajorPart != expected.Major || version.FileMinorPart != expected.Minor || version.FileBuildPart != expected.Build)
                throw new Vc14DllVersionMismatch(arch, item.File, version.FileVersion, info.Version);
        }
    }
}
