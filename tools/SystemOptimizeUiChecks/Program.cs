using System.Reflection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using DeltaNFD.ViewModels;

var token = Guid.NewGuid();
var isolation = Path.Combine(Path.GetTempPath(), "DeltaNFD-SystemOptimize-" + token.ToString("N"));
Directory.CreateDirectory(isolation);
File.WriteAllText(Path.Combine(isolation, "isolated-test.marker"), token.ToString("D"));
typeof(AppDataPaths).GetMethod("ConfigureIsolatedDataRoot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [isolation, token]);
BxCatalog.ConfigureAssetRoot(Path.Combine(isolation, "no-assets"));
int passed = 0;
void Check(bool ok, string why) { if (!ok) throw new Exception(why); passed++; }
BxItemVm Row(string name)
{
    var r = new BxItemVm { Id = "test/" + name, DisplayName = name, Item = new() { Name = name, SectionId = "test" }, GameReadOnly = false, IsOn = true, HasUserIntent = true };
    r.SetActual(BxState.Off); return r;
}
BxDbViewModel Model(params BxItemVm[] rows)
{
    var vm = new BxDbViewModel { GameReadOnly = false };
    var cache = (Dictionary<string,List<BxItemVm>>)typeof(BxDbViewModel).GetField("_sectionRows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
    cache.Clear(); cache["test"] = rows.ToList(); vm.Items.Clear(); foreach (var r in rows) vm.Items.Add(r);
    return vm;
}
foreach (var scenario in new[] { "cancel", "throw", "cleanup-fail", "cleanup-throw", "success" })
{
    int applied = 0, cleanup = 0; bool excluded = false; bool? notStarted = null;
    ServiceLocator.Bx = FakeProxy.CreateFake<IBxService>((m,a) => { if (m.Name == "ApplyItemAsync") applied++; return FakeProxy.Default(m,a); });
    ServiceLocator.DefenderGuard = FakeProxy.CreateFake<IDefenderGuardService>((m,a) => {
        if (m.Name == "PreflightAsync") return Task.FromResult<DefenderPreflightReport?>(new() { DefenderPresent = true, InitialRealTimeProtectionOn = true });
        if (m.Name == "PostApplyAsync") {
            cleanup++; notStarted = (bool)a![0]!;
            if (scenario == "cleanup-throw") throw new IOException("fixture cleanup error");
            if (scenario == "cleanup-fail") return Task.FromResult(new DefenderPostApplyResult { CleanupSucceeded = false, Message = "fixture pending" });
            excluded = false; return Task.FromResult(new DefenderPostApplyResult());
        }
        return FakeProxy.Default(m,a);
    });
    var vm = Model(Row("security"));
    vm.DefenderGuardHook = _ => { excluded = true; if (scenario == "throw") throw new IOException("fixture recheck failure"); return Task.FromResult(scenario == "success" ? DefenderGuardDecision.Proceed : DefenderGuardDecision.Cancel); };
    await vm.ApplyChangesAsync();
    Check(applied == (scenario == "success" ? 1 : 0), scenario + " wrong system submissions");
    Check(cleanup == 1 && notStarted == (applied == 0), scenario + " wrong cleanup intent");
    Check(!vm.IsApplyingBatch && vm.Items[0].CanEdit, scenario + " lock not released");
    Check(excluded == scenario.StartsWith("cleanup"), scenario + " cleanup responsibility");
    if (excluded) Check(vm.StatusText.Contains("安全收尾"), "pending cleanup must be visible");
}
var preflight = new TaskCompletionSource<DefenderPreflightReport?>(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
int preflights = 0; var writes = new List<string>();
ServiceLocator.DefenderGuard = FakeProxy.CreateFake<IDefenderGuardService>((m,a) => { if (m.Name == "PreflightAsync") { preflights++; return preflight.Task; } return FakeProxy.Default(m,a); });
ServiceLocator.Bx = FakeProxy.CreateFake<IBxService>((m,a) => m.Name is "ApplyItemAsync" or "ApplyGearAsync" ? Apply((BxItem)a![0]!, a[1]!) : FakeProxy.Default(m,a));
async Task<OperationResult> Apply(BxItem item, object value) { writes.Add(item.Name + ":" + value); if (item.Name == "first") { entered.SetResult(); await release.Task; } return OperationResult.Ok("ok"); }
var second = Row("second"); var gear = Row("gear"); gear.SetGears(new BxGearInfo { CurrentIndex = 0, Options = [new() { Label="a", Value="0" },new() { Label="b", Value="1" }] });
var batch = Model(Row("first"), second, gear); batch.GearChanged(gear,1);
var pending = batch.ApplyChangesAsync(); await batch.ApplyChangesAsync(); await batch.RestoreAllAsync();
Check(preflights == 1 && batch.IsApplyingBatch && !second.CanEdit, "preflight must reserve batch");
preflight.SetResult(null); await entered.Task; await batch.ToggleAsync(second,false); batch.GearChanged(gear,0);
Check(second.IsOn && gear.SelectedGearIndex == 1, "UI cannot change running batch");
// Even an out-of-band binding notification cannot redirect an approved write.
second.IsOn = false; gear.SelectedGearIndex = 0; release.SetResult(); await pending;
Check(writes.SequenceEqual(["first:True","second:True","gear:1"]), "write directions/gear differ from approved snapshot");
Check(!batch.IsApplyingBatch && second.CanEdit, "lock released after apply");
int calls=0; var sizes = (0,0,'?');
ServiceLocator.Pagefile = FakeProxy.CreateFake<IPagefileService>((m,a) => { if (m.Name == "SetCustomAsync") { calls++; sizes = ((int)a![0]!, (int)a[1]!, (char)a[2]!); } return FakeProxy.Default(m,a); });
var pf = new SystemOptimizeViewModel { GameReadOnly=false }; await pf.LoadPagefileDrivesAsync();
foreach (var (min,max) in new[] { ("4194320","4194320"), ("16","2097152"), ("16","4194320"), ("-1","16"), ("16","15"), ("17","16"), ("x","16"), ("16","9999999999999999") })
{ pf.PagefileMinText=min; pf.PagefileMaxText=max; Check(pf.PreparePagefile() is null && calls==0, "invalid size "+min+"/"+max); }
pf.PagefileMinText="16";pf.PagefileMaxText="2097151";var request=pf.PreparePagefile()!;
pf.PagefileMaxText="32"; pf.SelectedDriveIndex=-1; await pf.ApplyPagefileAsync(request);
Check(calls==1 && sizes==(16384,2147482624,'C'), "apply must use the checked and confirmed drive/sizes");
Check(pf.PreparePagefile() is null, "reject missing drive before confirmation");
Console.WriteLine($"System optimization UI: {passed} checks passed; real view-models and isolated test data, no system writes.");
