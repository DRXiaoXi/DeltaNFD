using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Views;
using Microsoft.UI.Xaml.Controls;

UiContext.Run(Run);
static async Task Run()
{
var checks = 0;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
async Task Idle(LowMemoryRecoveryPage page)
{
    bool Busy() => (bool)typeof(LowMemoryRecoveryPage).GetField("_busy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (Busy() && DateTime.UtcNow < deadline) await Task.Delay(2);
    Check(!Busy(), "page operation completed");
}
async Task<LowMemoryRecoveryPage> Load()
{
    var page = new LowMemoryRecoveryPage(); page.Load(); await Idle(page); return page;
}

Fixture.Reset();
var page = await Load();
Check(Fixture.Requests.Count == 0 && Fixture.Dialogs.Count == 0, "initialization and programmatic sync never write or ask for confirmation");
Check(page.PrefetchToggle.IsEnabled && !page.PrefetchToggle.IsOn && !page.ApplyButton.IsEnabled, "known prefetch remains independently operable with unknown SysMain and memory features");
Fixture.Confirm = () => Task.FromResult(ContentDialogResult.None);
page.PrefetchToggle.IsOn = true; await Idle(page);
Check(!page.PrefetchToggle.IsOn && Fixture.Requests.Count == 0, "cancel restores observed switch state and sends nothing");
Check(Fixture.Dialogs.Single().DefaultButton == ContentDialogButton.Close, "confirmation defaults to cancel");
Fixture.Confirm = () => Task.FromResult(ContentDialogResult.Primary);
page.PrefetchToggle.IsOn = true; await Idle(page);
Check(Fixture.Requests.Count == 1 && !Fixture.Requests[0].RestorePrefetch && page.PrefetchToggle.IsOn, "confirmed enable submits one disable then renders post-read");
Check(page.PrefetchToggle.IsEnabled && page.PrefetchRestoreButton.IsEnabled, "saved original enables reverse operation");
page.PrefetchToggle.IsOn = false; await Idle(page);
Check(Fixture.Requests.Count == 2 && Fixture.Requests[1].RestorePrefetch && !page.PrefetchToggle.IsOn, "confirmed off submits restore only and reflects resulting state");
page.Unload();

Fixture.Reset(); Fixture.Status = Fixture.Status with { PrefetchDisabled = true };
page = await Load();
Check(page.PrefetchToggle.IsOn && !page.PrefetchToggle.IsEnabled && page.PrefetchHint.Text.Contains("没有"), "externally-disabled state without backup is true but cannot restore guessed defaults");
page.Unload();

Fixture.Reset(); Fixture.Status = Fixture.Status with { PrefetchDisabled = null };
page = await Load();
Check(!page.PrefetchToggle.IsEnabled && (string?)page.PrefetchToggle.OffContent == "状态待确认", "unknown target is labeled unknown and cannot mutate");
page.Unload();

Fixture.Reset(); Fixture.BackupUnreadable = true;
page = await Load();
Check(!page.PrefetchToggle.IsEnabled && page.PrefetchHint.Text.Contains("备份未能读取"), "unknown backup is not fabricated no-backup or writable");
page.Unload();

Fixture.Reset(); Fixture.Blocker = "game running";
page = await Load(); Check(!page.PrefetchToggle.IsEnabled, "game gate blocks independent switch");
Fixture.Blocker = null; DeltaNFD.Services.Backends.PageBackendSessions.Manager.Notify();
Check(page.PrefetchToggle.IsEnabled && Fixture.Requests.Count == 0, "gate refresh only updates controls");
Fixture.Confirm = () => { Fixture.Blocker = "game started"; return Task.FromResult(ContentDialogResult.Primary); };
page.PrefetchToggle.IsOn = true; await Idle(page);
Check(Fixture.Requests.Count == 0 && !page.PrefetchToggle.IsOn, "game starts during confirmation: no submission or optimistic success");
page.Unload();

Fixture.Reset(); page = await Load();
var pending = new TaskCompletionSource<ContentDialogResult>();
Fixture.Confirm = () => pending.Task;
page.PrefetchToggle.IsOn = true;
Check(page.BusyRing.IsActive && !page.PrefetchToggle.IsEnabled, "confirmation owns busy state");
page.Unload(); pending.SetResult(ContentDialogResult.Primary); await Idle(page);
Check(Fixture.Requests.Count == 0, "leaving during confirmation prevents submission");

Fixture.Reset(); page = await Load();
Fixture.Invoke = _ => Task.FromResult(BackendResult.Failed(BackendCommands.SystemLowMemoryApply, "fixture refused"));
page.PrefetchToggle.IsOn = true; await Idle(page);
Check(!page.PrefetchToggle.IsOn && page.ResultText.Text.Contains("refused") && page.Progress.Value == 0, "refused write leaves actual switch off and failure visible");
page.Unload();

Fixture.Reset(); page = await Load();
Fixture.Invoke = _ => throw new IOException("connection lost");
page.PrefetchToggle.IsOn = true; await Idle(page);
Check(!page.PrefetchToggle.IsEnabled && (string?)page.PrefetchToggle.OffContent == "状态待确认" && page.StatusBar.Message.Contains("connection lost"),
    "lost outcome requires recheck and never shows requested state as success");
page.Unload();

Fixture.Reset(); Fixture.HasBackup = true; page = await Load();
page.RestoreOriginal(); await Idle(page);
Check(Fixture.Requests.Single().RestorePrefetch, "partial prefetch configuration has independent restore entry");
page.Unload();

Fixture.Reset(); Fixture.Status = Fixture.Status with { TotalBytes = 32UL * 1024 * 1024 * 1024 };
page = await Load(); Check(!page.PrefetchToggle.IsEnabled, "low-memory entry retains capacity restriction"); page.Unload();
Console.WriteLine($"Low-memory page: {checks} checks passed; actual page code, inert controls and fake backend only; no real GUI or system writes.");
}
