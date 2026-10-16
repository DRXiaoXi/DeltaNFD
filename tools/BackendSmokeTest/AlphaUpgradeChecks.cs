using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Updates;

internal static class AlphaUpgradeChecks
{
    internal static bool RuntimeEnabled(string layout)
    {
        using var stream = File.OpenRead(Path.Combine(layout, "DeltaNFD.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "RuntimeUiEnabled")
                return bool.Parse(blob.ReadSerializedString()!);
        }
        throw new IOException("Missing RuntimeUiEnabled metadata");
    }

    internal static async Task RunAsync(string source, string output)
    {
        if (AppVersion.Current.Minor != 9193) throw new IOException("Build this isolated client as 0.9193.");
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new test output directory.");
        Directory.CreateDirectory(output);
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(output, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(output, id);
        AppSettingsStore.Update(s => { s.UpdateChannel = UpdateChannel.Internal; s.UpdateServer = UpdateServer.SelfHosted; s.UpdateMode = UpdateMode.ChangedFiles; });
        var install = Path.Combine(output, "install"); Directory.CreateDirectory(install);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(install, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        if (RuntimeEnabled(install)) throw new IOException("0.9193 must hide runtime UI.");
        var config = UpdateService.LoadConfig(null);
        var service = new UpdateService(config, downloadRoot: Path.Combine(output, "downloads"), installRoot: install);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var urls = new List<string>();
        var check = await service.CheckSourcesAsync(config, AppSettingsStore.Read(), AppVersion.Current, true,
            (url, token) => { urls.Add(url); return UpdateService.FetchUpdateSourceAsync(url, config, token); }, timeout.Token);
        if (!check.HasUpdate || check.Manifest?.DisplayVersion != "Alpha0.9194"
            || !urls.SequenceEqual(new[] { UpdateService.OfficialInternalManifestUrl })) throw new IOException("Internal Alpha discovery failed: " + check.Message);
        var release = check.Manifest;
        var download = await service.DownloadAsync(release, cancellationToken: timeout.Token);
        if (!download.Success || !download.IsIncremental) throw new IOException("Changed-file download failed: " + download.Message);
        var reference = release.FileUpdate!;
        var manifest = FileUpdateRules.Read(File.ReadAllBytes(download.InstallerPath), reference.Sha256);
        var changes = FileTransaction.Changed(install, manifest);
        FileTransaction.Apply(install, Path.Combine(Path.GetDirectoryName(download.InstallerPath)!, "payload"), manifest,
            Path.Combine(output, "transaction"), () =>
            {
                if (!RuntimeEnabled(install) || FileTransaction.Changed(install, manifest).Count != 0) throw new IOException("Full-version feature/hash check failed.");
                // The production stateless startup probe needs a protected session,
                // but does not touch registry, settings, user services or runtimes.
                var probeSession = ProtectedSessions.Create();
                var nonce = Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(probeSession, "health-request.json"), JsonSerializer.Serialize(new { nonce, root = install, version = manifest.Version }));
                var start = new ProcessStartInfo(Path.Combine(install, "DeltaNFD.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--verify-file-update", probeSession, nonce }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Probe did not start.");
                if (!process.WaitForExit(45000) || process.ExitCode != 0 || File.ReadAllText(Path.Combine(probeSession, "health-ok.txt")) != nonce)
                    throw new IOException("Stateless product startup probe failed.");
            });
        AppSettingsStore.Update(s => s.UpdateMode = UpdateMode.FullInstaller);
        var full = await service.DownloadAsync(release, cancellationToken: timeout.Token);
        if (!full.Success || full.IsIncremental) throw new IOException("Self-hosted full download failed: " + full.Message);
        var version = FileVersionInfo.GetVersionInfo(full.InstallerPath);
        if (version.FileMinorPart != 9194) throw new IOException("Full installer PE version mismatch.");
        var publicResult = service.Evaluate(release, new AppSettings { UpdateChannel = UpdateChannel.Public }, AppVersion.Current);
        if (publicResult.HasUpdate || service.Evaluate(release, AppSettingsStore.Read(), new Version(0, 9194)).HasUpdate)
            throw new IOException("Channel/same-version filtering failed.");
        var report = new { checkedUtc = DateTimeOffset.UtcNow, from = "Alpha0.9193", to = "Alpha0.9194", urls,
            changedFiles = changes.Count, compressedBytes = changes.GroupBy(f => f.Sha256).Sum(g => g.First().DownloadSize),
            fullInstallerBytes = new FileInfo(full.InstallerPath).Length, runtimeHiddenBefore = true, runtimeEnabledAfter = true,
            isolatedTransaction = "Committed", startupProbe = "passed", installerExecuted = false, installedRegistryChanged = false };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
