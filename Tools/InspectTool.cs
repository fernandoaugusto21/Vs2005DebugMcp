using EnvDTE;
using Vs2005DebugMcp.Dte;

namespace Vs2005DebugMcp.Tools;

public sealed record StackFrameInfo(string FunctionName, string? Module, string? Language);

public sealed record LocalVariableInfo(string Name, string Type, string Value);

/// <summary>
/// Tools de inspecao (call stack, locals). Todas exigem o debugger em
/// modo break — ver <see cref="RequireBreakModeAsync"/>.
/// </summary>
public static class InspectTool
{
    public static async Task RequireBreakModeAsync(DteSession session)
    {
        var mode = await session.GetDebugModeAsync().ConfigureAwait(false);
        if (mode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException(
                $"Debugger nao esta em modo break (modo atual: {mode}). " +
                "Chame vs_get_status antes de inspecionar call stack/locals/expressoes.");
        }
    }

    public static async Task<IReadOnlyList<StackFrameInfo>> GetCallStackAsync(DteSession session)
    {
        await RequireBreakModeAsync(session).ConfigureAwait(false);

        return await session.InvokeAsync(dte =>
        {
            var frames = dte.Debugger.CurrentThread.StackFrames;

            var result = new List<StackFrameInfo>();
            foreach (StackFrame frame in frames)
            {
                result.Add(new StackFrameInfo(frame.FunctionName, TryGet(() => frame.Module), TryGet(() => frame.Language)));
            }
            return (IReadOnlyList<StackFrameInfo>)result;
        }).ConfigureAwait(false);
    }

    // Expression.Value pode ser gigante para objetos complexos (ex. um
    // DataTable inteiro serializado como string) — truncar evita travar o
    // consumidor MCP com uma resposta descomunal. Ver risco anotado no
    // HANDOFF.md.
    private const int MaxValueLength = 4000;

    public static async Task<IReadOnlyList<LocalVariableInfo>> GetLocalsAsync(DteSession session, int stackFrameIndex)
    {
        await RequireBreakModeAsync(session).ConfigureAwait(false);

        return await session.InvokeAsync(dte =>
        {
            StackFrame target;
            if (stackFrameIndex == 0)
            {
                // Caminho mais barato e o default implicito do protocolo
                // original: nao precisa enumerar StackFrames.
                target = dte.Debugger.CurrentStackFrame;
            }
            else
            {
                var frames = dte.Debugger.CurrentThread.StackFrames;
                StackFrame? found = null;
                var index = 0;
                foreach (StackFrame frame in frames)
                {
                    if (index == stackFrameIndex)
                    {
                        found = frame;
                        break;
                    }
                    index++;
                }

                target = found ?? throw new InvalidOperationException(
                    $"stack_frame_index {stackFrameIndex} fora do alcance (thread atual tem {index} frame(s)).");
            }

            var result = new List<LocalVariableInfo>();
            foreach (Expression expr in target.Locals)
            {
                var type = TryGet(() => expr.Type) ?? "<erro ao ler tipo>";
                var value = TruncateValue(TryGet(() => expr.Value));
                result.Add(new LocalVariableInfo(expr.Name, type, value));
            }
            return (IReadOnlyList<LocalVariableInfo>)result;
        }).ConfigureAwait(false);
    }

    private static string TruncateValue(string? value)
    {
        if (value is null)
        {
            return "<erro ao ler valor>";
        }
        if (value.Length <= MaxValueLength)
        {
            return value;
        }
        return value[..MaxValueLength] + $"... [truncado, {value.Length} chars no total]";
    }

    // EnvDTE.StackFrame (interface base, unica disponivel sem risco de
    // mismatch de versao) nao expoe arquivo/linha-fonte — so
    // FunctionName/Module/Language/Locals/Arguments/ReturnType. Mapear
    // pra file+line exigiria achar o breakpoint correspondente ou uma
    // API mais rica (StackFrame2, se existir no VS2005); validar contra
    // a IDE real antes de tentar. Ver HANDOFF.md.
    private static string? TryGet(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }
}
