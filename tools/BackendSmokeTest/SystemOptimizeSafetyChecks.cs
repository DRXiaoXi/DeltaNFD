using System.Reflection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

internal static class SystemOptimizeSafetyChecks
{
    public static async Task RunAsync()
    {
        var token = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-optimize-fixes-" + token.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root,token);
        int count=0;
        void Check(bool ok,string why) { if (!ok) throw new Exception(why); count++; }
        // Only this unique, disposable HKCU fixture is writable. No actual paging-file path is opened.
        var sub = @"Software\DeltaNFD\RecoveryChecks\" + token.ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(sub);
            const string value = "PagingFiles";
            string[] original = [@"C:\pagefile.sys 1024 2048", @"D:\pagefile.sys 4096 8192"];
            string[] automatic = [@"?:\pagefile.sys"];
            var backup = new TweakBackupStore(Path.Combine(root,"pagefile.json"));
            key.SetValue(value, original, RegistryValueKind.MultiString);
            PagefileService.SetWithBackup(key,backup,automatic);
            Check(((string[])key.GetValue(value)!).SequenceEqual(automatic),"automatic replaced the test configuration");
            Check(PagefileService.DecodeOriginalEntries(backup.GetAllStrict().Single()).SequenceEqual(original),"first change to automatic saved both drives");
            PagefileService.SetWithBackup(key,backup,[@"C:\pagefile.sys 16384 32768"]);
            Check(PagefileService.DecodeOriginalEntries(backup.GetAllStrict().Single()).SequenceEqual(original),"custom change retains the first original");
            key.SetValue(value,original,RegistryValueKind.MultiString);
            var badFile = Path.Combine(root,"corrupt.json"); File.WriteAllText(badFile,"invalid-json");
            bool refused=false;
            try { PagefileService.SetWithBackup(key,new TweakBackupStore(badFile),automatic); } catch { refused=true; }
            Check(refused && ((string[])key.GetValue(value)!).SequenceEqual(original) && File.ReadAllText(badFile)=="invalid-json","backup failure must not write or replace evidence");
            key.SetValue(value,"unknown type",RegistryValueKind.String);
            var fresh = new TweakBackupStore(Path.Combine(root,"missing.json")); refused=false;
            try { PagefileService.SetWithBackup(key,fresh,automatic); } catch (InvalidDataException) { refused=true; }
            Check(refused && Equals(key.GetValue(value),"unknown type") && fresh.GetAllStrict().Count==0,"wrong existing type is not treated as missing");
            key.DeleteValue(value); PagefileService.SetWithBackup(key,fresh,automatic);
            Check(fresh.GetAllStrict().Single().ValueKind==RegistryValueKind.None,"verified absent paging-file value backs up absence");

            var capture = typeof(BxService).GetMethod("BackupReg",BindingFlags.NonPublic | BindingFlags.Static)!;
            var op = new BxTweak { Path=@"HKCU\" + sub + "\\" + new string('x',256), Key="synthetic" };
            refused=false; try { capture.Invoke(null,[op]); } catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { refused=true; }
            Check(refused && BxBackupStore.ReadAll().Count==0,"capture exception propagates without a fabricated backup");
            op.Path=@"HKCU\"+sub; capture.Invoke(null,[op]);
            Check(BxBackupStore.ReadAll().Single().ValueKind==0,"actually missing registry value can be recorded");
            key.SetValue("existing",17,RegistryValueKind.DWord); op.Key="existing"; capture.Invoke(null,[op]);
            Check(BxBackupStore.ReadAll().Single(x=>x.ValueName=="existing").Data=="17","existing registry original preserved");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub,throwOnMissingSubKey:false); }
        await DefenderSessionChecks.RunAsync(Check);
        foreach (var response in new (int, string?, string?)[]
        { (1,null,"unreadable"), (1,"{\"av\":true,\"rtp\":false,\"tamper\":false}","failed"),
          (0,"{\"av\":true}",null), (0,"{\"av\":true,\"rtp\":false,\"tamper\":null}",null) })
        {
            DefenderGuard.PowerShellForTests = _ => response;
            try { bool refused=false; try { await DefenderGuard.RefreshProtectionStateAsync(); } catch(IOException){ refused=true; } Check(refused,"failed or incomplete Defender status cannot pass recheck"); }
            finally { DefenderGuard.PowerShellForTests=null; }
        }
        Console.WriteLine($"System optimization safety: {count} checks passed; isolated files, owned HKCU fixture and simulated Defender only.");
    }
}
