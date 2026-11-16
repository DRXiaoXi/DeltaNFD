using DeltaNFD.Backends;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services.Backends;

/// <summary>Cache deletion uses the page-owned backend; completion only reads cache sizes and updates the software's own record.</summary>
internal sealed class BackendManualAsdService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    internal Task<ManualAsdSession> StartAsync(GameInstallationSelection selection) => StartCoreAsync(selection);
    private async Task<ManualAsdSession> StartCoreAsync(GameInstallationSelection selection)
    {
        var context = await Task.Run(() =>
        {
            using var operation = GameTargetService.Default.BeginOperation();
            return ManualAsdInspection.ReadContext();
        });
        return BackendResults.RequireData<ManualAsdSession>(await _caller.InvokeAsync(BackendCommands.SystemManualAsdStart,
            new ManualAsdStartArgs { Selection = selection, ExpectedContextId = ManualAsdStore.ContextId(context) }, null, CancellationToken.None), "开始手动 ASD 判定");
    }
    internal Task<(bool Supported, ManualAsdSession? Session, string Error)> ReadAsync() => Task.Run(() =>
    {
        using var operation = GameTargetService.Default.BeginOperation();
        var context = ManualAsdInspection.ReadContext();
        try { return (context.Supported, ManualAsdInspection.Read(ManualAsdStore.Default, () => context), ""); }
        catch (Exception ex) { return (context.Supported, (ManualAsdSession?)null, "进度读取失败，请重新开始判定：" + ex.Message); }
    });
    internal Task<ManualAsdSession> FinishAsync(string id) => Task.Run(() =>
    {
        using var operation = GameTargetService.Default.BeginOperation();
        return ManualAsdInspection.Finish(ManualAsdStore.Default, id, ManualAsdInspection.ReadContext);
    });
    internal Task<ManualAsdSession> CancelAsync(string id) => Task.Run(() => ManualAsdInspection.Cancel(ManualAsdStore.Default, id));
}
