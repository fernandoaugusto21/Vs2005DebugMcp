using System.Text.Json.Nodes;
using Vs2005DebugMcp.Dte;
using Vs2005DebugMcp.McpProtocol;
using Vs2005DebugMcp.Tools;

// Fase 1 (MVP): conexao + tools de leitura. Fase 2 (step/go/stop),
// antecipada para ser validada na mesma rodada da VDI, e a unica que
// modifica estado do debugger. Ver README.md e HANDOFF.md para o que falta
// (Fases 3-4).

using var session = new DteSession();
var connected = false;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var server = new JsonRpcServer(stdin, stdout);

server.RegisterHandler("initialize", _ => Task.FromResult<JsonNode?>(new JsonObject
{
    ["protocolVersion"] = "2024-11-05",
    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
    ["serverInfo"] = new JsonObject
    {
        ["name"] = "Vs2005DebugMcp",
        ["version"] = "0.2.0-phase2",
    },
}));

server.RegisterHandler("tools/list", _ =>
{
    var tools = new JsonArray();
    foreach (var tool in ToolSchemas.All)
    {
        tools.Add(tool.ToJson());
    }
    return Task.FromResult<JsonNode?>(new JsonObject { ["tools"] = tools });
});

server.RegisterHandler("tools/call", async Task<JsonNode?> (@params) =>
{
    var name = @params?["name"]?.GetValue<string>()
        ?? throw new InvalidOperationException("tools/call sem 'name'.");
    var arguments = @params?["arguments"];

    async Task<string> InvokeToolAsync()
    {
        if (!connected)
        {
            await session.ConnectAsync().ConfigureAwait(false);
            connected = true;
        }

        return name switch
        {
            "vs_get_status" => await StatusTool.GetStatusAsync(session).ConfigureAwait(false),
            "vs_get_call_stack" => JsonSerializerResult(await InspectTool.GetCallStackAsync(session).ConfigureAwait(false)),
            "vs_get_locals" => JsonSerializerResult(await InspectTool.GetLocalsAsync(
                session, arguments?["stack_frame_index"]?.GetValue<int>() ?? 0).ConfigureAwait(false)),
            "vs_read_output_window" => await OutputTool.ReadWindowAsync(
                session, arguments?["pane"]?.GetValue<string>() ?? "Debug").ConfigureAwait(false),
            "vs_list_output_panes" => JsonSerializerResult(await OutputTool.ListPanesAsync(session).ConfigureAwait(false)),
            "vs_step" => JsonSerializerResult(await StepTool.StepAsync(
                session,
                arguments?["mode"]?.GetValue<string>() ?? throw new InvalidOperationException("vs_step exige 'mode'."),
                arguments?["wait_for_break"]?.GetValue<bool>() ?? true).ConfigureAwait(false)),
            "vs_go" => JsonSerializerResult(await StepTool.GoAsync(
                session, arguments?["wait_for_break"]?.GetValue<bool>() ?? false).ConfigureAwait(false)),
            "vs_stop_debugging" => JsonSerializerResult(await StepTool.StopAsync(session).ConfigureAwait(false)),
            _ => throw new InvalidOperationException($"Tool desconhecido: {name}"),
        };
    }

    string text;
    try
    {
        text = await InvokeToolAsync().ConfigureAwait(false);
    }
    catch (DteConnectionException) when (connected)
    {
        // A sessao anterior pode ter caido (VS2005 fechado/reaberto
        // durante o teste manual na VDI) — tenta reconectar uma unica
        // vez via ROT antes de propagar o erro.
        connected = false;
        text = await InvokeToolAsync().ConfigureAwait(false);
    }

    return new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
        },
    };
});

await server.RunAsync().ConfigureAwait(false);

static string JsonSerializerResult<T>(T value) =>
    System.Text.Json.JsonSerializer.Serialize(value);
