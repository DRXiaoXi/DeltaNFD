using DeltaNFD.Native;

internal static class PluginImportPickerChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool success, string message) { if (!success) throw new Exception(message); count++; }
        void Reject(string path)
        {
            var rejected = false;
            try { FolderPickerDialog.ValidatePluginPackageSelection(path); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid selection must be rejected: " + path);
        }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PickerChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "插件 包.DNFDPLUGIN");
            File.WriteAllBytes(package, []);
            Check(FolderPickerDialog.ValidatePluginPackageSelection(null) is null, "取消保持 null");
            Check(FolderPickerDialog.ValidatePluginPackageSelection(package) == package, "中文空格路径及大小写扩展名支持");
            Reject("relative.dnfdplugin");
            Reject(Path.Combine(root, "missing.dnfdplugin"));
            Reject(root);
            var wrong = Path.Combine(root, "package.zip");
            File.WriteAllBytes(wrong, []);
            Reject(wrong);
            var missingOwner = false;
            try { FolderPickerDialog.PickPluginPackage(nint.Zero); }
            catch (InvalidOperationException) { missingOwner = true; }
            Check(missingOwner, "主窗口缺失应报错，不创建无属主对话框");
            Check(new FileInfo(package).Length == 0, "选择验证不执行或解包文件；内容交由 importer 验证");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"插件选择验证通过：{count} 项；未弹真实窗口、未运行插件。");
    }
}
