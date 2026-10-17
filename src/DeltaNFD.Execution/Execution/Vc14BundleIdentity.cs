using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record Vc14BundleProof(string Path, string Sha256, string BundleId, string Version,
    string Architecture, bool Verified, string Reason, string Name = "");

/// <summary>Reads a signed Burn engine's own identity. Never loads or executes the package.</summary>
internal static class Vc14BundleIdentity
{
    internal static Vc14BundleProof Read(string path, string client, string arch)
    {
        var hash = ""; var id = ""; var version = ""; var name = "";
        try
        {
            if (!Path.IsPathFullyQualified(path) || arch is not ("x64" or "x86")) throw new IOException("缓存路径或目标架构无效");
            for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存路径包含重解析点");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 512 or > 268435456) throw new IOException("缓存文件大小超出核验范围");
            var info = FileVersionInfo.GetVersionInfo(path);
            version = info.FileVersion ?? "";
            name = info.FileDescription ?? "";
            if (!Vc14PackageTrust.Verify(path, version, arch)) throw new IOException("缓存未通过微软签名、C++ 14.x 原包版本或架构核验");
            hash = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            id = ReadId(stream);
            if (!SameId(id, client)) throw new IOException("原包内置 BundleId 与依赖引用不一致");
            return new(path, hash, id, version, arch, true, "微软签名 C++ 14.x 原包及内置 BundleId 已核验", name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or BadImageFormatException or System.Security.Cryptography.CryptographicException)
        { return new(path, hash, id, version, arch, false, ex.Message, name); }
    }

    // WiX v3 section.cpp: BURN_SECTION_HEADER version 2. The identity must be
    // within a PE section covered by Authenticode, never a searched overlay GUID.
    internal static string ReadId(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = pe.PEHeaders;
        var sections = headers.SectionHeaders.Where(s => s.Name == ".wixburn").ToArray();
        var certificate = headers.PEHeader?.CertificateTableDirectory ?? default;
        if (sections.Length != 1 || certificate.RelativeVirtualAddress <= 0 || certificate.Size <= 0
            || (long)certificate.RelativeVirtualAddress + certificate.Size > stream.Length)
            throw new IOException("未发现唯一且带签名的 Burn 身份段");
        var section = sections[0];
        if (section.SizeOfRawData < 48 || section.PointerToRawData < headers.PEHeader!.SizeOfHeaders
            || (long)section.PointerToRawData + section.SizeOfRawData > certificate.RelativeVirtualAddress)
            throw new IOException("Burn 身份段不在签名覆盖的有效范围内");
        stream.Position = section.PointerToRawData;
        Span<byte> header = stackalloc byte[48]; stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00f14300
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2)
            throw new IOException("不支持的 Burn 身份格式，未猜测包装归属");
        var id = new Guid(header.Slice(8, 16));
        if (id == Guid.Empty) throw new IOException("原包 BundleId 为空");
        return id.ToString("B");
    }

    internal static bool SameId(string first, string second) => Guid.TryParse(first, out var a)
        && Guid.TryParse(second, out var b) && a != Guid.Empty && a == b;

    internal static IReadOnlyList<string> FixedCacheCandidates(string cacheRoot, string client, string arch)
    {
        if (!Guid.TryParseExact(client, "B", out var id) || id == Guid.Empty || arch is not ("x64" or "x86")) return [];
        // Only the exact referenced GUID's fixed cache directory; no recursive cache scan.
        return new[] { client, id.ToString("B"), id.ToString("B").ToUpperInvariant() }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(folder => Path.Combine(cacheRoot, folder, "vc_redist." + arch + ".exe")).ToArray();
    }
}
