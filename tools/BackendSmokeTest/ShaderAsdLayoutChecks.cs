using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Services;
using DeltaNFD.Inspection;
using DeltaNFD.ViewModels;

internal static class ShaderAsdLayoutChecks
{
    internal static void Run()
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        var root=Path.Combine(Path.GetTempPath(),"DeltaNFD-asd-capacity-"+Guid.NewGuid().ToString("N"));
        var cache=Path.Combine(root,"GameVer","SM6","DXCache");Directory.CreateDirectory(cache);
        const long mb=1024L*1024;
        ShaderStatus Status(string version)=>new(){IsNvidia=true,DriverState=ShaderDriverState.Ok,DriverVersion=version,DriverAdvice="fixture",IsGameRunning=false};
        NvShaderCacheReport Good()=>new(){Scopes=[new("全局设置","default",null,"fixture"),new("三角洲程序设置","default",null,"fixture")]};
        foreach(var version in new[]{"621.00","621.14","621.99","622.00","700.00"})
        foreach(var sizes in new long[][]{[256*mb,1],[256*mb,256*mb],[256*mb,mb,0],[256*mb,512*mb,1]})
        {
            foreach(var f in Directory.GetFiles(cache))File.Delete(f);
            for(var i=0;i<sizes.Length;i++)NvphFixtureFiles.Raw(Path.Combine(cache,i+".nvph"),sizes[i]);
            var diag=new ShaderInspection(null,()=>new(ShaderGpuDetectionState.Nvidia,version)).DiagnoseAt(root,true);
            Check(diag.Level==ShaderDiagLevel.Normal&&diag.CacheLayout==ShaderCacheLayout.Unspecified&&diag.SystemCache is null,"size rule is green without automatic ASD inspection");
            Check(!diag.Summary.Contains("ASD"),"summary never advertises ASD from a layout");
            var health=DashboardShaderHealth.Evaluate(Status(version),diag,Good(),true);
            Check(health.State==DashboardHealthState.Normal&&!health.Text.Contains("ASD"),"homepage green is cache health only");
            var custom=new NvShaderCacheReport{Scopes=[new("全局设置","custom",10240,"fixture"),new("三角洲程序设置","default",null,"fixture")]};
            Check(DashboardShaderHealth.Evaluate(Status(version),diag,custom,true).State==DashboardHealthState.Abnormal,"independent nondefault settings stay red");
            var wire=JsonSerializer.Deserialize<ShaderDiagnosis>(JsonSerializer.Serialize(diag))!;
            Check(wire.CacheLayout==ShaderCacheLayout.Unspecified&&wire.Level==ShaderDiagLevel.Normal,"IPC does not invent ASD");
        }
        Directory.CreateDirectory(Path.Combine(root,"OldGameVer"));
        Check(new ShaderInspection(null,()=>new(ShaderGpuDetectionState.Nvidia,"621.14")).DiagnoseAt(root).Level==ShaderDiagLevel.Abnormal,"version residue remains abnormal");
        Check((int)ShaderDiagLevel.Warning==5&&(int)ShaderCacheLayout.Asd==2,"historical protocol values retained");
        Console.WriteLine($"Automatic/manual ASD separation: {count} checks passed. Owned sparse fixtures only: {root}");
    }
}
