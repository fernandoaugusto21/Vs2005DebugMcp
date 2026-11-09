using System.Text.Json.Nodes;

namespace Vs2005DebugMcp.McpProtocol;

/// <summary>
/// Descricao de um tool MCP: nome, descricao e schema JSON de entrada.
/// Usado tanto para responder "tools/list" quanto para validar o
/// dispatch em "tools/call".
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
    };
}

public static class ToolSchemas
{
    // Fase 1 — somente leitura. Fase 2 (step/go/stop) foi antecipada para
    // ser validada na mesma rodada da VDI. Fases 3-4 (breakpoints/evaluate/
    // attach) ficam para depois.

    public static readonly ToolDefinition GetStatus = new(
        "vs_get_status",
        "Retorna o modo atual do debugger do VS2005 (design/break/run).",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    public static readonly ToolDefinition GetCallStack = new(
        "vs_get_call_stack",
        "Lista os stack frames da thread atual (Debugger.CurrentProgram/CurrentThread). Requer modo break.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    public static readonly ToolDefinition GetLocals = new(
        "vs_get_locals",
        "Lista as variaveis locais de um stack frame. Requer modo break.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["stack_frame_index"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Indice do stack frame (0 = frame atual). Default 0.",
                    ["default"] = 0,
                },
            },
        });

    public static readonly ToolDefinition ReadOutputWindow = new(
        "vs_read_output_window",
        "Le o texto de uma pane da Output Window do VS2005.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pane"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Nome da pane (ex: 'Debug'). Default 'Debug'.",
                    ["default"] = "Debug",
                },
            },
        });

    public static readonly ToolDefinition ListOutputPanes = new(
        "vs_list_output_panes",
        "Lista os nomes das panes disponiveis na Output Window (util quando o VS2005 esta instalado em outro idioma e o nome da pane de debug nao e 'Debug').",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    // Fase 2 — controle de execucao (muda estado do debugger).

    public static readonly ToolDefinition Step = new(
        "vs_step",
        "Executa um passo (into/over/out) a partir do modo break. Retorna o modo antes e depois.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("into", "over", "out"),
                    ["description"] = "Tipo de passo.",
                },
                ["wait_for_break"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Esperar o debugger voltar a modo break antes de responder. Default true.",
                    ["default"] = true,
                },
            },
            ["required"] = new JsonArray("mode"),
        });

    public static readonly ToolDefinition Go = new(
        "vs_go",
        "Continua a execucao ate o proximo breakpoint ou o fim (em modo design, inicia o debug como F5). " +
        "Com wait_for_break=true o servidor fica bloqueado ate o break — nenhum outro tool responde nesse intervalo.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["wait_for_break"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Esperar o proximo break/fim antes de responder. Default false.",
                    ["default"] = false,
                },
            },
        });

    public static readonly ToolDefinition StopDebugging = new(
        "vs_stop_debugging",
        "Encerra a sessao de debug (Debugger.Stop) e espera voltar ao modo design.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    public static IReadOnlyList<ToolDefinition> All { get; } = new[]
    {
        GetStatus,
        GetCallStack,
        GetLocals,
        ReadOutputWindow,
        ListOutputPanes,
        Step,
        Go,
        StopDebugging,
    };
}
