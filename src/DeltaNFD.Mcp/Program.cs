using System.ComponentModel;
using System.Text.Json;
using DeltaNFD.AgentBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

var hostArgs = args;
if (hostArgs.Length == 1 && hostArgs[0] == "--catalog")
{
    Console.WriteLine(JsonSerializer.Serialize(AgentCatalog.Actions, AgentPipe.Json));
    return;
}
if (hostArgs.Length != 0 && (hostArgs.Length != 2 || hostArgs[0] != "--app-dir"))
{
    Console.Error.WriteLine("Usage: DeltaNFD.Mcp.exe [--app-dir <DeltaNFD directory>]");
    Environment.ExitCode = 2; return;
}
AgentTools.AppDirectory = hostArgs.Length == 2 ? Path.GetFullPath(hostArgs[1]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<AgentTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class AgentTools
{
    internal static string AppDirectory = "";

    [McpServerTool(Name = "deltanfd_status", ReadOnly = true, OpenWorld = false), Description("Check the running DeltaNFD application, version, admin/game state and unresolved operations. Open DeltaNFD and enable Settings > MCP for this session first.")]
    public Task<JsonElement> Status(CancellationToken cancellationToken) => Call("status", new { }, cancellationToken);

    [McpServerTool(Name = "deltanfd_list_actions", ReadOnly = true, OpenWorld = false), Description("List supported public actions, their inputSchema, read/write scope and description. Empty module lists all. Never assume hidden or unlisted backend commands are available.")]
    public object ListActions([Description("Optional module: gpu, system, runtime, ace, procman, app.")] string? module = null) =>
        AgentCatalog.Actions.Where(a => string.IsNullOrEmpty(module) || a.Module == module).ToArray();

    [McpServerTool(Name = "deltanfd_submit_action", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Submit one allowlisted action with arguments matching its inputSchema. Returns an operation ID immediately; queued/awaiting_confirmation/running are not success. Mutations require local UI confirmation. Poll get_operation. Reuse the SAME requestId with identical arguments after a transport error within this enabled session; never automatically resubmit after an app restart, re-enabling access, or an unknown result.")]
    public Task<JsonElement> SubmitAction(string action, Dictionary<string, JsonElement> arguments,
        [Description("Unique ID (1–128 characters). Reuse exactly for transport retries to prevent duplicate writes.")] string requestId,
        CancellationToken cancellationToken) => Call("submit", new { action, arguments, requestId }, cancellationToken);

    [McpServerTool(Name = "deltanfd_get_operation", ReadOnly = true, OpenWorld = false), Description("Read action progress and the original backend result. completed/partial/failed/cancelled/requires_review are terminal. requires_review means preserve evidence and use the application's review flow; never retry or claim success. Results belong to the current enabled application session.")]
    public Task<JsonElement> GetOperation(string operationId, CancellationToken cancellationToken) => Call("operation", new { operationId }, cancellationToken);

    [McpServerTool(Name = "deltanfd_cancel_operation", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Cancel a queued or not-yet-confirmed operation. Running system writes are allowed to finish; this tool never kills a backend or installer.")]
    public Task<JsonElement> CancelOperation(string operationId, CancellationToken cancellationToken) => Call("cancel", new { operationId }, cancellationToken);

    private static async Task<JsonElement> Call(string method, object arguments, CancellationToken token)
    {
        try { return await AgentPipe.CallAsync(AppDirectory, method, arguments, token); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
            or UnauthorizedAccessException or Win32Exception)
        { throw new McpException(ex.Message); }
        catch (IOException)
        {
            throw new McpException("与主程序连接中断，不能据此认定操作未执行。请先查询原操作；同一次接入内仅可用相同 requestId 和参数重查。主程序重启或重新开启接入后，请在设置核对实际状态，勿自动重提修改。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new McpException("连接主程序超时。请确认同目录软件已开启本次接入；已提交的修改请查询原操作或到主程序核对，勿更换 requestId 重试。");
        }
    }
}
