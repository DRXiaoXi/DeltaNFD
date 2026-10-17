using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimeSxsIdentity(string Name, string Version, string Architecture, string PublicKeyToken, string? Language);

/// <summary>Resolve installed VC80/VC90 assemblies using an isolated activation context. Never load DLLs or call repair-triggering MSI APIs.</summary>
internal static class RuntimeSxsResolver
{
    internal static void Validate(RuntimeSxsIdentity identity)
    {
        if (!Regex.IsMatch(identity.Name, @"^Microsoft\.VC(?:80|90)\.(?:CRT|ATL|MFC|MFCLOC|OpenMP)$", RegexOptions.IgnoreCase)
            || !Version.TryParse(identity.Version, out var version) || version.Build < 0 || version.Revision < 0
            || identity.Architecture is not ("x86" or "amd64") || identity.PublicKeyToken != "1fc8b3b9a1e18e3b"
            || identity.Language is not null && !Regex.IsMatch(identity.Language, @"^[a-zA-Z0-9*\-]{1,35}$"))
            throw new InvalidDataException("旧版 C++ SxS 程序集身份无效，未尝试路径猜测。");
    }
    internal static string BuildManifest(RuntimeSxsIdentity identity)
    {
        Validate(identity); XNamespace ns = "urn:schemas-microsoft-com:asm.v1";
        var dependency = new XElement(ns + "assemblyIdentity", new XAttribute("type", "win32"), new XAttribute("name", identity.Name),
            new XAttribute("version", identity.Version), new XAttribute("processorArchitecture", identity.Architecture), new XAttribute("publicKeyToken", identity.PublicKeyToken));
        if (!string.IsNullOrEmpty(identity.Language)) dependency.Add(new XAttribute("language", identity.Language));
        return new XDocument(new XElement(ns + "assembly", new XAttribute("manifestVersion", "1.0"),
            new XElement(ns + "assemblyIdentity", new XAttribute("type", "win32"), new XAttribute("name", "DeltaNFD.RuntimeProbe"),
                new XAttribute("version", "1.0.0.0"), new XAttribute("processorArchitecture", identity.Architecture)),
            new XElement(ns + "dependency", new XElement(ns + "dependentAssembly", dependency)))).ToString();
    }
    internal static string Resolve(RuntimeSxsIdentity identity)
    {
        var manifest = BuildManifest(identity);
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-sxs-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "probe.manifest");
        var context = new IntPtr(-1);
        try
        {
            File.WriteAllText(path, manifest, new System.Text.UTF8Encoding(false));
            var args = new ACTCTX { Size = (uint)Marshal.SizeOf<ACTCTX>(), Source = path };
            context = CreateActCtxW(ref args);
            if (context == new IntPtr(-1)) throw new IOException($"SxS {identity.Name}/{identity.Architecture} 无法激活：Win32 {Marshal.GetLastWin32Error()}。未触发安装。");
            var detail = ReadContext(context);
            if (detail.AssemblyCount is < 2 or > 128) throw new InvalidDataException("SxS 激活上下文程序集数异常。");
            for (uint index = 2; index <= detail.AssemblyCount; index++)
            {
                var info = ReadAssembly(context, index);
                if (!MatchesIdentity(info.Encoded, identity)) continue;
                var windowsSxs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
                var directory = ResolveDirectory(windowsSxs, info.Directory);
                // Also validate the actual resolved manifest, independent of directory naming conventions.
                ValidateResolvedManifest(info.Manifest, identity);
                return directory;
            }
            throw new IOException("没有找到与请求身份匹配的已激活 C++ 共享程序集。");
        }
        finally
        {
            if (context != new IntPtr(-1)) ReleaseActCtx(context);
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root); // only the unique, empty probe directory
        }
    }
    internal static string ResolveDirectory(string root, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("SxS 程序集没有目录。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.IsPathFullyQualified(directory) ? directory : Path.Combine(root, directory));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("SxS 解析目录不在系统组件存储中。");
        for (var parent = new DirectoryInfo(full); parent is not null && parent.FullName.Length >= fullRoot.TrimEnd(Path.DirectorySeparatorChar).Length; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("SxS 目录包含链接，未读取转向目标。");
        return full;
    }
    private static bool MatchesIdentity(string encoded, RuntimeSxsIdentity identity)
    {
        var name = encoded.Split(',')[0].Trim();
        return name.Equals(identity.Name, StringComparison.OrdinalIgnoreCase)
            && encoded.Contains("processorArchitecture=\"" + identity.Architecture + "\"", StringComparison.OrdinalIgnoreCase)
            && encoded.Contains("publicKeyToken=\"" + identity.PublicKeyToken + "\"", StringComparison.OrdinalIgnoreCase);
    }
    internal static void ValidateResolvedManifest(string path, RuntimeSxsIdentity requested)
    {
        var store = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(store, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SxS 清单不在系统组件存储中。");
        using var reader = XmlReader.Create(full, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var document = XDocument.Load(reader); XNamespace ns = "urn:schemas-microsoft-com:asm.v1";
        var actual = document.Root?.Element(ns + "assemblyIdentity") ?? throw new InvalidDataException("SxS 清单缺少身份。");
        if (!string.Equals((string?)actual.Attribute("name"), requested.Name, StringComparison.OrdinalIgnoreCase)
            || (string?)actual.Attribute("processorArchitecture") != requested.Architecture
            || (string?)actual.Attribute("publicKeyToken") != requested.PublicKeyToken
            || (string?)actual.Attribute("type") != "win32"
            || !Version.TryParse((string?)actual.Attribute("version"), out var version) || version < Version.Parse(requested.Version))
            throw new InvalidDataException("解析到的 SxS 身份、架构或版本不符合要求。");
    }
    private static IntPtr Query(IntPtr context, IntPtr index, uint kind)
    {
        QueryActCtxW(0, context, index, kind, IntPtr.Zero, 0, out var required);
        if (required < 16 || required > 1024 * 1024) throw new IOException("SxS 查询缓冲长度无效：" + Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)required);
        if (QueryActCtxW(0, context, index, kind, buffer, required, out _)) return buffer;
        Marshal.FreeHGlobal(buffer); throw new IOException("SxS 只读查询失败：" + Marshal.GetLastWin32Error());
    }
    private static ContextInfo ReadContext(IntPtr context)
    {
        var buffer = Query(context, IntPtr.Zero, 2);
        try { return Marshal.PtrToStructure<ContextInfo>(buffer); } finally { Marshal.FreeHGlobal(buffer); }
    }
    private static (string Encoded, string Directory, string Manifest) ReadAssembly(IntPtr context, uint index)
    {
        var pointer = Marshal.AllocHGlobal(4); Marshal.WriteInt32(pointer, (int)index);
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Query(context, pointer, 3); var info = Marshal.PtrToStructure<AssemblyInfo>(buffer);
            return (Marshal.PtrToStringUni(info.Encoded) ?? "", Marshal.PtrToStringUni(info.Directory) ?? "", Marshal.PtrToStringUni(info.Manifest) ?? "");
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ACTCTX
    { public uint Size, Flags; public string Source; public ushort Architecture, Language; public string? AssemblyDirectory, Resource, Application; public IntPtr Module; }
    [StructLayout(LayoutKind.Sequential)] private struct ContextInfo
    { public uint Flags, Format, AssemblyCount, ManifestType, ManifestChars, ConfigurationType, ConfigurationChars, AppType, AppChars; public IntPtr Manifest, Configuration, App; }
    [StructLayout(LayoutKind.Sequential)] private struct AssemblyInfo
    {
        public uint Flags, IdentityLength, ManifestType, ManifestLength; public long ManifestTime;
        public uint PolicyType, PolicyLength; public long PolicyTime;
        public uint MetadataIndex, ManifestMajor, ManifestMinor, PolicyMajor, PolicyMinor, DirectoryLength;
        public IntPtr Encoded, Manifest, Policy, Directory; public uint FileCount;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateActCtxW(ref ACTCTX context);
    [DllImport("kernel32.dll")] private static extern void ReleaseActCtx(IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryActCtxW(uint flags, IntPtr context, IntPtr index, uint kind, IntPtr data, nuint length, out nuint required);
}
