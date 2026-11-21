using DeltaNFD.Services.CrashDiagnostics;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var now = DateTimeOffset.Parse("2026-11-19T05:00:00Z");
var id = Guid.NewGuid();
var wer = CrashEvent.Parse($"""
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="{CrashAnalysis.Wer}"/><EventID>1001</EventID><TimeCreated SystemTime="2026-11-19T04:00:00Z"/><EventRecordID>66</EventRecordID></System><EventData><Data Name="param3">{id:B}</Data><Data Name="param2">C:\private_user\memory.dmp</Data><Data Name="param1">0x00000116 (0xffffffffffffffff, 0x22, 0x0, 0x4)</Data></EventData></Event>
""");
CrashEvent E(string provider, int number, params (string,string)[] fields) => new(provider, number, wer.Time, 77, fields.ToDictionary(f => f.Item1, f => f.Item2));
var crash = CrashAnalysis.Identify(wer)!;
Check(crash.Code == 0x116 && crash.Args.SequenceEqual(new ulong[] { ulong.MaxValue, 0x22, 0, 4 }) && crash.ReportId == id, "localized XML / 64-bit args");
var power = E(CrashAnalysis.Power, 41, ("BugcheckCode", "159"), ("BugcheckParameter1", "0x3"), ("BugcheckParameter2", "0x4"), ("BugcheckParameter3", "0x5"), ("BugcheckParameter4", "0x6"));
Check(CrashAnalysis.Identify(power) is { Code: 0x9F, Args: [3,4,5,6] }, "decimal power stop code");
foreach (var val in new[]{"0", "0x9F", "abc", "", "-1", "4294967296"}) Check(CrashAnalysis.Identify(E(CrashAnalysis.Power,41,("BugcheckCode",val))) == null,"invalid power code " + val);
Check(CrashAnalysis.Identify(wer with { Provider = CrashAnalysis.AppWer }) == null, "collision 1001");
foreach(var c in new uint[]{0x116,0x119,0x9F,0xD1,0xA,0x50,0x1A,0x124,0x133,0x7A,0xEF,0x3B}) Check(!CrashAnalysis.Meaning(c).Contains("尚无"), "supported stop");
Check(CrashAnalysis.Meaning(0xDEAD).Contains("尚无"), "unknown stop");
var app = E(CrashAnalysis.AppWer,1001,("EventName","BlueScreen"),("ReportId",id.ToString()),("P1","116"),("P3","22"),("Bucket","0x116_IMAGE_fixture.sys"));
Check(CrashAnalysis.BucketDriver(crash,app) == "fixture.sys", "report id and bucket image");
foreach(var (key,val) in new[]{("ReportId",Guid.NewGuid().ToString()),("P1","117"),("P3","3"),("EventName","LiveKernelEvent"),("Bucket","0123467"),("Bucket", "IMAGE_C:\\private\\driver.sys")})
{ var d=new Dictionary<string,string>(app.Data){[key]=val}; Check(CrashAnalysis.BucketDriver(crash, app with {Data=d})==null,"reject " + key); }
var dbg="BUGCHECK_CODE: 116\r\nBUGCHECK_P1: ffffffff`ffffffff\nBUGCHECK_P2: 22\nBUGCHECK_P3: 0\nBUGCHECK_P4: 4\nIMAGE_NAME: sample.SYS";
Check(CrashAnalysis.DebuggerDriver(crash,dbg).Driver=="sample.SYS","debugger matching");
foreach(var bad in new[]{dbg.Replace("116","117"),dbg.Replace("22","23"),dbg.Replace("BUGCHECK_P3", "OTHER"),dbg.Replace("sample.SYS","ntkrnlmp.exe"), dbg.Replace("sample.SYS","C:\\secrets\\sample.sys"),dbg+"\nIMAGE_NAME: other.sys",dbg+new string('x',65536)}) Check(CrashAnalysis.DebuggerDriver(crash,bad).Driver==null,"debugger reject");
Check(CrashAnalysis.DebuggerDriver(crash with {Args=[]},dbg).Driver==null,"missing event args");
string Render(CrashEvent[] anchors, CrashEvent[]? context=null, CrashEvent[]? reports=null, string text="", string? notice=null) => CrashReport.Analyze(text, default, (log, filter, from, to, limit, ct) => {
 Check(to<=now && from<=to && limit<=256,"bounded query");
 return filter == CrashLogReader.Anchors ? new(anchors.ToList(),notice) : new((filter == CrashLogReader.AppReports ? reports : context)?.ToList() ?? []);
}, now);
Check(Render([]).Contains("未找到"),"empty"); Check(Render([],notice:"访问被拒绝").Contains("无法判断"),"failed not empty");
var unknown = E(CrashAnalysis.Power,41,("BugcheckCode","0")) with {Time = now};
Check(Render([unknown]).Contains("不能据此确认"),"dirty shutdown not bsod");
Check(Render([E("EventLog",6008)]).Contains("没有可核对"),"6008 not bsod");
var display = E("Display",4101); var report = Render([wer, unknown],[display,display with {Provider="unrelated"},display with {Time=wer.Time.AddMinutes(-11)},wer],[app],dbg);
Check(report.Contains("0x00000116") && report.Contains("另有更新"),"latest confirmed");
Check(report.Contains("fixture.sys") && report.Contains("sample.SYS") && report.Contains("不同的模块"),"conflicting evidence");
Check(report.Contains("memory.dmp")&&!report.Contains("private_user"),"dump basename only");
Check(report.Split("Display / 4101").Length==2 && !report.Contains("unrelated"),"provider, window, dedup anchor");
Check(Render([power], [display]).Contains("没有指向具体驱动"),"timeline not attribution");
Check(Render([wer],Enumerable.Range(0,18).Select(i=>display with {Record=100+i, Time=wer.Time.AddSeconds(i)}).ToArray()).Contains("仅列距记录时间最近的 12 条"),"display budget");
Check(Render([wer],notice:"截断").Contains("读取限制\n截断"),"partial preserved");
const string displayXml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Display'/><EventID>4101</EventID><TimeCreated SystemTime='2026-11-19T04:00:00Z'/><EventRecordID>99</EventRecordID></System><EventData><Data>fixtureGPU</Data><Data /></EventData></Event>";
Check(Render([wer],[CrashEvent.Parse(displayXml)]).Contains("显示驱动 fixtureGPU 曾超时并恢复（不是蓝屏归因）"), "contextual display module");
Check(!Render([wer],[CrashEvent.Parse(displayXml.Replace("fixtureGPU", "private path"))]).Contains("private path"), "no free text from display event");
Check(CrashAnalysis.Corroboration(0x116,[display]) != null, "graphics corroboration");
Check(CrashAnalysis.Corroboration(0x9f,[display]) == null, "no graphics inference for power transition");
Check(CrashAnalysis.Corroboration(0x116,[display with {Provider="unknown"}]) == null, "no inference on colliding ID");
Check(CrashAnalysis.Corroboration(0x124,[E("Microsoft-Windows-WHEA-Logger",19)]) != null, "hardware corroboration");
Check(CrashAnalysis.Corroboration(0x7A,[E("stornvme",129)]) != null, "storage corroboration");
// Synthetic, sparse 32/64-bit kernel dump headers: parse only bounded header bytes and never claim a driver from a code alone.
var fixture = Path.Combine(Path.GetTempPath(), "crash-dump-check-" + Guid.NewGuid().ToString("N") + ".dmp");
try
{
    byte[] Header(string marker, int codeOffset, int argOffset, int argWidth, uint code, ulong[] args)
    {
        var bytes=new byte[0x2000]; System.Text.Encoding.ASCII.GetBytes("PAGE").CopyTo(bytes,0); System.Text.Encoding.ASCII.GetBytes(marker).CopyTo(bytes,4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(argWidth==8?0x30:0x20), argWidth==8?0x8664u:0x14cu);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(codeOffset),code);
        for(int i=0;i<4;i++) if(argWidth==8) System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(argOffset+i*argWidth),args[i]); else System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(argOffset+i*argWidth),(uint)args[i]);
        return bytes;
    }
    var args64=new ulong[]{0xffffffffffffffff,0x22,0x0,0x4}; var h64=Header("DU64",0x38,0x40,8,0x116,args64);
    var filetime=new DateTimeOffset(2026,11,19,4,0,0,TimeSpan.Zero).ToFileTime(); System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(h64.AsSpan(0xFA8),filetime); File.WriteAllBytes(fixture,h64);
    var parsed=KernelDumpReader.Read(fixture,default); Check(parsed.Code==0x116&&parsed.Args.SequenceEqual(args64)&&parsed.Architecture=="x64"&&parsed.CrashTime?.UtcDateTime==new DateTime(2026,11,19,4,0,0,DateTimeKind.Utc),"64-bit dump header and time");
    Check(new FileInfo(fixture).Length==0x2000,"dump remains unchanged");
    var h32=Header("DUMP",0x28,0x2c,4,0x9f,[1,2,3,4]); File.WriteAllBytes(fixture,h32); var parsed32=KernelDumpReader.Read(fixture,default); Check(parsed32.Architecture=="x86"&&parsed32.Code==0x9f&&parsed32.Args.SequenceEqual(new ulong[]{1,2,3,4})&&parsed32.CrashTime==null,"32-bit dump header");
    foreach(var bad in new[]{new byte[0x2000],Header("NOPE",0x38,0x40,8,0x116,args64),Header("DU64",0x38,0x40,8,0,args64)}) { File.WriteAllBytes(fixture,bad); try { KernelDumpReader.Read(fixture,default); throw new Exception("malformed dump accepted"); } catch(InvalidDataException){checks++;} }
    File.WriteAllBytes(fixture,new byte[32]); try { KernelDumpReader.Read(fixture,default); throw new Exception("truncated dump accepted"); } catch(InvalidDataException){checks++;}
    File.WriteAllBytes(fixture,h64); try { KernelDumpReader.Read(fixture,new CancellationToken(true)); throw new Exception("cancel ignored"); } catch(OperationCanceledException){checks++;}
    var dump=KernelDumpReader.Read(fixture,default); var dumpReport=CrashReport.AnalyzeDump(dump,default,(log,filter,from,to,limit,ct)=> {
      Check(to-from<=TimeSpan.FromMinutes(20)&&limit<=256,"bounded dump event query");
      if(filter==CrashLogReader.Anchors)return new CrashBatch([wer with {Data=new Dictionary<string,string>(wer.Data) { ["param2"] = fixture }}]);
      if(filter==CrashLogReader.AppReports)return new CrashBatch([app]);
      return new CrashBatch([display]);
    });
    Check(dumpReport.Contains("PAGE / DU64")&&dumpReport.Contains("fixture.sys")&&dumpReport.Contains("匹配系统错误报告"),"direct dump report and exact event correlation");
    var mismatched=CrashReport.AnalyzeDump(dump with { Args=[ulong.MaxValue,0x22,3,4] },default,(log,filter,from,to,limit,ct)=>filter==CrashLogReader.Anchors ? new CrashBatch([wer with {Data=new Dictionary<string,string>(wer.Data) { ["param2"]=fixture }}]) : new CrashBatch([app]));
    Check(mismatched.Contains("转储参考时间前后 10 分钟") && mismatched.Contains("外来转储"),"dump WHEA local-source caveat");
    Check(!mismatched.Contains("fixture.sys")&&mismatched.Contains("没有可确认的责任驱动"),"reject single mismatched parameter");
    var live=h64[..]; live[0x1050]=0x10; var arm=h64[..]; arm[0x30]=0x64; arm[0x31]=0xaa;
    foreach(var invalid in new[]{h64[..4096], live, arm}) {
        File.WriteAllBytes(fixture,invalid); try {KernelDumpReader.Read(fixture,default); throw new Exception("invalid architecture, live or truncated header accepted");}catch(InvalidDataException){checks++;}
    }
    var noCorrelation=CrashReport.AnalyzeDump(dump,default,(log,filter,from,to,limit,ct)=>new CrashBatch(filter==CrashLogReader.Timeline?[display]:[]));
    Check(noCorrelation.Contains("不能单独证明")&&noCorrelation.Contains("没有可确认的责任驱动"),"no speculative driver attribution");
}
finally { File.Delete(fixture); }
// Provider-wide query is independent of crash anchors and of the generic timeline's ID list.
var hw = new[]{E(WheaReport.Provider,17), E(WheaReport.Provider,18,("ApicId","7")), E(WheaReport.Provider,47), E(WheaReport.Provider,3), E(WheaReport.Provider,999), E("Other",18), E(WheaReport.Provider,18) with {Time=now.AddDays(-31)}};
var recent = WheaReport.Recent(default,(ch,f,lo,hi,n,ct)=>{
    Check(ch=="System" && f==WheaReport.Filter && lo==now.AddDays(-30) && hi==now && n==256,"provider-wide bounded scan"); return new(hw.ToList()); },now);
Check(recent.Contains("已读取 5 条") && recent.Contains("已纠正 2 条") && recent.Contains("严重 1 条") && recent.Contains("信息 1 条") && recent.Contains("未识别类型 1 条"),"exact provider, bounded time, distinct known and unknown categories");
Check(recent.Contains("处理器核心 · APIC ID 7") && recent.Contains("已纠正 · 内存子系统"),"hardware location, corrected memory is not fatal");
Check(Render([],hw).Contains("已读取 5 条"),"WHEA without confirmed BSOD");
Check(Render([wer],hw).Contains("蓝屏记录时间前后 10 分钟"),"confirmed BSOD scope");
var partial=WheaReport.Recent(default,(ch,f,lo,hi,n,ct)=>new([],"无权限"),now);
Check(partial.Contains("无法确认") && partial.Contains("无权限") && !partial.Contains("未找到"),"denied not reported as clean");
var limited=WheaReport.Recent(default,(ch,f,lo,hi,n,ct)=>new(Enumerable.Range(1,20).Select(i=>E(WheaReport.Provider,17) with {Record=i}).ToList(),"读取上限"),now);
Check(limited.Contains("已读取 20 条") && limited.Split('•').Length==13 && limited.Contains("读取上限"),"truncated summary and bounded detail");
try { WheaReport.Recent(new CancellationToken(true),(ch,f,lo,hi,n,ct)=>throw new Exception("read after cancel")); throw new Exception("WHEA cancellation ignored"); } catch(OperationCanceledException){checks++;}
// Exercise Windows' actual XPath engine, without fetching user diagnostics or creating logs.
var future=new DateTimeOffset(2099,1,1,0,0,0,TimeSpan.Zero);
foreach(var f in new[]{CrashLogReader.Anchors,CrashLogReader.AppReports,CrashLogReader.Timeline,WheaReport.Filter}) {
 var batch=CrashLogReader.Read(f==CrashLogReader.AppReports?"Application":"System",f,future,future,1,default);
 Check(batch.Notice==null && batch.Events.Count==0,"native fixed empty window: "+batch.Notice);
}
try { CrashLogReader.Read("System",CrashLogReader.Anchors,now,now,1,new CancellationToken(true)); throw new Exception("cancellation ignored"); } catch(OperationCanceledException){checks++;}
Console.WriteLine($"Crash diagnostics: {checks} checks passed.");
