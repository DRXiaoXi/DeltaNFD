$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
public static class ByteSearch2 {
    public static int IndexOf(byte[] haystack, byte[] needle) {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++) {
            if (haystack[i] != needle[0]) continue;
            bool ok = true;
            for (int j = 1; j < needle.Length; j++) {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
    public static byte[] Slice(byte[] data, int offset, int length) {
        byte[] r = new byte[length];
        Array.Copy(data, offset, r, 0, length);
        return r;
    }
}
"@

$icoPath = Resolve-Path 'tools\DeltaNFD-v7.ico'
$ico = [System.IO.File]::ReadAllBytes($icoPath.Path)
$count = [BitConverter]::ToUInt16($ico, 4)
$e = 6 + ($count - 1) * 16
$size = [BitConverter]::ToUInt32($ico, $e + 8)
$off = [BitConverter]::ToUInt32($ico, $e + 12)
$needle = [ByteSearch2]::Slice($ico, $off + [int]($size / 2) - 64, 128)
Write-Output ("probe: v7 ico largest frame (256px PNG, " + $size + " bytes) mid 128 bytes")

$exe = Resolve-Path 'src\DeltaNFD\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\DeltaNFD.exe'
$bytes = [System.IO.File]::ReadAllBytes($exe.Path)
$pos = [ByteSearch2]::IndexOf($bytes, $needle)
if ($pos -ge 0) { Write-Output ("MATCH: Debug exe embeds v7 icon (offset " + $pos + ")") }
else { Write-Output "NO MATCH: Debug exe"; exit 1 }
