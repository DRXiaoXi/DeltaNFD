using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using DeltaNFD.Backends;
namespace BackendSmokeTest;

internal static class TimerRecoveryChecks
{
    public static void Run(string assets)
    {
        var count=0;
        void Check(bool ok,string name) { if(!ok) throw new Exception(name);count++; }
        const string entry="identifier {current}\n";
        foreach(var pair in new[]{("Yes","Yes"),("Yes",""),("","Yes"),("是","1")})
        {
            var text=entry+(pair.Item1.Length>0 ? "disabledynamictick "+pair.Item1+"\n" : "")+(pair.Item2.Length>0 ? "useplatformtick "+pair.Item2 : "");
            var parsed=TimerConfigurationInspection.ParseBcd(0,text);
            Check(parsed.BcdReadable && parsed.HasOverride,"任一覆盖即告警");
        }
        Check(!TimerConfigurationInspection.ParseBcd(1,entry).BcdReadable,"读失败不当默认");
        Check(!TimerConfigurationInspection.ParseBcd(0,"access denied").BcdReadable,"伪成功输出不当默认");
        Check(!TimerConfigurationInspection.ParseBcd(0,entry+"useplatformtick maybe").BcdReadable,"未知值不当默认");
        Check(TimerConfigurationInspection.ParseBcd(0,entry).BcdReadable,"两项不存在可确认默认");
        Check(new TimerRecoveryStatus{BcdReadable=true,DevicesReadable=true}.NeedsAttention==false,"正常不告警");
        Check(new TimerRecoveryStatus{BcdReadable=true,DevicesReadable=true,Devices=[new(@"ACPI\PNP0103\0",22)]}.NeedsAttention,"只有设备禁用仍告警");
        var normal = new TimerRecoveryStatus { BcdReadable=true, DevicesReadable=true };
        var normalNotice = TimerHealthPresentation.Create(normal);
        Check(!normalNotice.IsOpen && !normalNotice.IsError,"主页：两类检测正常不告警");
        foreach (var value in new[] { "Yes", "true", "1", "是", " YES " })
        {
            foreach (var dynamic in new[] { false, true })
            {
                var state = dynamic ? normal with { DynamicTick=value } : normal with { PlatformTick=value };
                var notice = TimerHealthPresentation.Create(state);
                var key = dynamic ? "disabledynamictick" : "useplatformtick";
                Check(notice.IsOpen && notice.IsError && notice.Message.Contains(key),"主页：任一开启即红色并显示具体键："+key+"="+value);
                Check(notice.Message.Contains("进入恢复页关闭") && notice.Message.Contains("手动重启"),"主页：红色提示包含关闭入口和重启要求");
            }
        }
        foreach (var value in new[] { "", "No", "false", "0", "否" })
            Check(!TimerHealthPresentation.Create(normal with { DynamicTick=value, PlatformTick=value }).IsOpen,"主页：未开启/未配置不报红");
        var both=TimerHealthPresentation.Create(normal with { DynamicTick="Yes", PlatformTick="Yes", PendingRestart=true });
        Check(both.IsError && both.Title=="启动计时器选项已开启" && both.Message.Contains("useplatformtick") && both.Message.Contains("disabledynamictick"),"待重启不遮蔽两项当前开启");
        Check(both.Message.Contains("仍需手动重启"),"当前开启同时保留恢复重启记录提示");
        var hpet=TimerHealthPresentation.Create(normal with { Devices=[new(@"ACPI\PNP0103\0",22)] });
        Check(hpet.IsOpen && !hpet.IsError && hpet.Title=="HPET 设备已禁用" && hpet.Message.Contains("另行勾选"),"保留独立HPET设备警告和确认");
        var combined=TimerHealthPresentation.Create(normal with { PlatformTick="Yes", Devices=[new(@"ACPI\PNP0103\0",22)] });
        Check(combined.IsError && combined.Message.Contains("useplatformtick") && combined.Message.Contains("HPET 设备被禁用"),"两类同时异常不遗漏设备提醒");
        foreach (var state in new[] { normal with { BcdReadable=false }, normal with { DevicesReadable=false },
            normal with { BcdReadable=false, PlatformTick="Yes" } })
        {
            var notice=TimerHealthPresentation.Create(state);
            Check(notice.IsOpen && !notice.IsError && notice.Title=="计时器状态待核对","无法确认时黄色，不冒充开启/正常");
        }
        var invalid=TimerConfigurationInspection.ParseBcd(0,"useplatformtick Yes");
        Check(!TimerHealthPresentation.Create(invalid).IsError,"无启动项标识的输出不能确认红色开启");
        Check(TimerHealthPresentation.Create(normal with { RecoveryIncomplete=true }).IsError,"未完成恢复记录继续保留警告");
        var pending=TimerHealthPresentation.Create(normal with { PendingRestart=true });
        Check(pending.IsOpen && !pending.IsError && pending.Title=="恢复后请重启电脑","配置已关闭但未重启仍明确提醒");
        var failed=TimerHealthPresentation.Create(normal with { BcdReadable=false, Error="拒绝访问" });
        Check(failed.Message.Contains("拒绝访问"),"保留读取失败原因");
        BxCatalog.ConfigureAssetRoot(Path.GetFullPath(assets));
        Check(BxCatalog.FindItem("basic","HPETName") is null && BxCatalog.GetSpecialOps("HPETName").Count==0,"目录完全移除旧优化");
        Check(BackendModules.IsMutating(BackendModules.System,BackendCommands.SystemTimerRecover)
            && BackendModules.RequiresWriteIntent(BackendModules.System,BackendCommands.SystemTimerRecover),"恢复受写前意图门禁保护");
        foreach(var pair in new[]{("Yes","Yes"),("","Yes"),("Yes",""),("","")})
        {
            var fake=new Fake{Dynamic=pair.Item1,Platform=pair.Item2};var result=Run(fake);
            Check(result.Success && fake.Dynamic=="" && fake.Platform=="" && fake.Last?.Completed==true,"两项独立恢复/不存在幂等");
            Check(fake.Writes.Count==(pair.Item1.Length>0?1:0)+(pair.Item2.Length>0?1:0),"仅写当前存在的固定键");
            Check(result.RequiresReboot==fake.Writes.Any(),"有修改才需重启");
        }
        var noAdmin=new Fake{Blocked="not admin"};Check(!Run(noAdmin).Success && noAdmin.Writes.Count==0,"权限/游戏阻断无写入");
        var unreadable=new Fake{Readable=false};Check(!Run(unreadable).Success && unreadable.Writes.Count==0,"读失败无写入");
        var saveFail=new Fake{SaveFail=true};Check(!Run(saveFail).Success && saveFail.Writes.Count==0,"先备份失败无写入");
        var fail=new Fake{DeleteFail=true};Check(!Run(fail).Success && fail.Writes.Count==1 && fail.Last?.Completed==false,"失败停止并保留未完成记录");
        var timeout=new Fake{Timeout=true};Check(Run(timeout).OutcomeUnknown && timeout.Last?.Completed==false,"超时即使读到不存在也不冒充成功");
        var changed=new Fake();var stale=changed.Read();changed.Platform="No";
        Check(!TimerRecoveryEngine.Run(changed,new(stale.Fingerprint,false),default).Success && changed.Writes.Count==0,"指纹变化拒绝");
        var device=new Fake{Devices=[new(@"ACPI\PNP0103\0",22)]};
        Check(Run(device,true).Success && device.Devices[0].ProblemCode==0,"明确选择后恢复固定HPET设备");
        var skip=new Fake{Devices=[new(@"ACPI\PNP0103\0",22)]};
        Check(Run(skip).Success && skip.Devices[0].Disabled && skip.EnabledDevices==0,"未选择不改设备");
        var badDevice=new Fake{Devices=[new(@"PCI\OTHER\0",22)]};Check(!Run(badDevice,true).Success && badDevice.Writes.Count==0,"禁止其他设备");
        var game=new Fake{GameAfterFirst=true};Check(!Run(game).Success && game.Writes.Count==1,"游戏中途启动停止后续写入");
        using var cancel=new CancellationTokenSource();var cancelled=new Fake{CancelAfterWrite=cancel};
        Check(!TimerRecoveryEngine.Run(cancelled,new(cancelled.Read().Fingerprint,false),cancel.Token).Success && cancelled.Last?.Completed==false,"取消保留记录");
        var restart=new Fake{Dynamic="",Platform="",PendingRestart=true};Check(Run(restart).RequiresReboot && restart.Last?.Changed==true,"未重启重复核对也保留持久重启提示");
        Console.WriteLine("PASS timer recovery: "+count+" checks; fake BCD/devices only.");
    }
    private static OperationResult Run(Fake f,bool devices=false)=>TimerRecoveryEngine.Run(f,new(f.Read().Fingerprint,devices),default);
    private sealed class Fake : ITimerRecoveryEnvironment
    {
        public string Dynamic="Yes",Platform="Yes";
        public bool Readable=true,SaveFail,DeleteFail,Timeout,GameAfterFirst,PendingRestart;
        public string? Blocked;
        public CancellationTokenSource? CancelAfterWrite;
        public List<TimerDeviceState> Devices=[];
        public List<string> Writes=[];
        public int EnabledDevices;
        public TimerRecoveryRecord? Last;
        public string? Blocker()=>Blocked ?? (GameAfterFirst && Writes.Count>0 ? "game running" : null);
        public TimerRecoveryStatus Read()
        {
            var v=new TimerRecoveryStatus{BcdReadable=Readable,DynamicTick=Dynamic,PlatformTick=Platform,DevicesReadable=true,Devices=Devices.ToList(),BootId="boot-1",PendingRestart=PendingRestart};
            return v with{Fingerprint=TimerConfigurationInspection.Fingerprint(v)};
        }
        public void Save(TimerRecoveryRecord r) {if(SaveFail)throw new IOException("backup failed");Last=r;}
        public int DeleteValue(string n)
        {
            if(n is not ("disabledynamictick" or "useplatformtick"))throw new Exception("unexpected key");
            Writes.Add(n);if(DeleteFail)return 5;
            if(n=="disabledynamictick")Dynamic="";else Platform="";
            CancelAfterWrite?.Cancel();return Timeout?-1:0;
        }
        public int EnableDevice(string id) { EnabledDevices++;Devices=Devices.Select(d=>d.InstanceId==id ? d with{ProblemCode=0}:d).ToList();return 0; }
    }
}
