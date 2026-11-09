using EnvDTE;
using Vs2005DebugMcp.Dte;

namespace Vs2005DebugMcp.Tools;

public sealed record ExecutionResult(string Action, string ModeBefore, string ModeAfter);

/// <summary>
/// Fase 2 — controle de execucao (step/go/stop). Diferente dos tools de
/// leitura, estes MUDAM o estado do debugger. Todos usam so membros de
/// <c>EnvDTE.Debugger</c> (interface base, presente desde o VS2002), sem
/// depender de <c>Debugger2</c>/<c>Debugger3</c> — mesmo raciocinio de
/// risco de versao do <see cref="InspectTool"/>.
///
/// Atencao com <c>wait_for_break</c>: a chamada COM bloqueia a thread STA
/// ate o debugger voltar a modo break (ou o programa terminar). Enquanto
/// isso, NENHUM outro tool responde (todos passam pelo mesmo
/// <see cref="StaDispatcher"/>). Por isso <c>vs_go</c> tem default false.
/// </summary>
public static class StepTool
{
    public static async Task<ExecutionResult> StepAsync(DteSession session, string mode, bool waitForBreak)
    {
        await InspectTool.RequireBreakModeAsync(session).ConfigureAwait(false);

        return await session.InvokeAsync(dte =>
        {
            var debugger = dte.Debugger;
            var before = debugger.CurrentMode;
            switch (mode)
            {
                case "into":
                    debugger.StepInto(waitForBreak);
                    break;
                case "over":
                    debugger.StepOver(waitForBreak);
                    break;
                case "out":
                    debugger.StepOut(waitForBreak);
                    break;
                default:
                    throw new InvalidOperationException($"mode invalido: '{mode}' (use into, over ou out).");
            }
            return new ExecutionResult($"step_{mode}", ModeName(before), ModeName(debugger.CurrentMode));
        }).ConfigureAwait(false);
    }

    public static async Task<ExecutionResult> GoAsync(DteSession session, bool waitForBreak)
    {
        return await session.InvokeAsync(dte =>
        {
            var debugger = dte.Debugger;
            var before = debugger.CurrentMode;
            if (before == dbgDebugMode.dbgRunMode)
            {
                throw new InvalidOperationException("Debugger ja esta em modo run — nada a continuar.");
            }
            // Em design mode, Go() equivale ao F5: inicia o projeto de
            // startup da solution aberta (cenario de debug local).
            debugger.Go(waitForBreak);
            return new ExecutionResult("go", ModeName(before), ModeName(debugger.CurrentMode));
        }).ConfigureAwait(false);
    }

    public static async Task<ExecutionResult> StopAsync(DteSession session)
    {
        return await session.InvokeAsync(dte =>
        {
            var debugger = dte.Debugger;
            var before = debugger.CurrentMode;
            if (before == dbgDebugMode.dbgDesignMode)
            {
                throw new InvalidOperationException("Nenhuma sessao de debug ativa (modo design).");
            }
            debugger.Stop(true);
            return new ExecutionResult("stop", ModeName(before), ModeName(debugger.CurrentMode));
        }).ConfigureAwait(false);
    }

    public static string ModeName(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design",
        dbgDebugMode.dbgBreakMode => "break",
        dbgDebugMode.dbgRunMode => "run",
        _ => "unknown",
    };
}
