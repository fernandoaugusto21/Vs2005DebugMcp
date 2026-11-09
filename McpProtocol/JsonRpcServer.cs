using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vs2005DebugMcp.McpProtocol;

/// <summary>
/// Framing e dispatch de mensagens MCP (JSON-RPC 2.0) sobre stdio, usando
/// o framing do transporte stdio do MCP: uma mensagem JSON por linha,
/// delimitada por '\n', sem quebras de linha internas. (NAO e o framing
/// "Content-Length" do LSP — um cliente MCP como o Claude Code nunca envia
/// esse header, e o servidor ficaria esperando para sempre.)
/// </summary>
public sealed class JsonRpcServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Dictionary<string, Func<JsonNode?, Task<JsonNode?>>> _handlers = new();
    private readonly object _writeLock = new();

    public JsonRpcServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public void RegisterHandler(string method, Func<JsonNode?, Task<JsonNode?>> handler)
    {
        _handlers[method] = handler;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var pending = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                break; // stdin fechado
            }

            pending.Add(HandleMessageAsync(message));
            pending.RemoveAll(t => t.IsCompleted);
        }

        // stdin fechado: termina de responder o que ja estava em andamento
        // antes de o processo sair.
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task HandleMessageAsync(JsonNode message)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        var @params = message["params"];

        if (method is null)
        {
            return;
        }

        if (!_handlers.TryGetValue(method, out var handler))
        {
            if (id is not null)
            {
                WriteError(id, -32601, $"Method not found: {method}");
            }
            return;
        }

        try
        {
            var result = await handler(@params).ConfigureAwait(false);
            if (id is not null)
            {
                WriteResult(id, result);
            }
        }
        catch (Exception ex)
        {
            if (id is not null)
            {
                WriteError(id, -32000, ex.Message);
            }
        }
    }

    private void WriteResult(JsonNode id, JsonNode? result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result ?? new JsonObject(),
        };
        WriteMessage(envelope);
    }

    private void WriteError(JsonNode id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        WriteMessage(envelope);
    }

    private void WriteMessage(JsonNode envelope)
    {
        // JsonNode.ToJsonString() nao indenta, entao o JSON sai sem quebras
        // de linha internas — requisito do framing por linha.
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString() + "\n");

        lock (_writeLock)
        {
            _output.Write(bytes, 0, bytes.Length);
            _output.Flush();
        }
    }

    private async Task<JsonNode?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null; // EOF
            }

            if (line.Length == 0)
            {
                continue; // linha vazia entre mensagens — tolerada
            }

            try
            {
                return JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                // Linha que nao e JSON (ex. header "Content-Length" de um
                // cliente LSP-style) — ignora em vez de derrubar o servidor.
                continue;
            }
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var single = new byte[1];

        while (true)
        {
            var read = await _input.ReadAsync(single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (single[0] == (byte)'\n')
            {
                break;
            }

            if (single[0] != (byte)'\r')
            {
                buffer.Add(single[0]);
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
