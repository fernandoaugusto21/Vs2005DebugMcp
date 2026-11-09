using EnvDTE;
using Vs2005DebugMcp.Dte;

namespace Vs2005DebugMcp.Tools;

public static class OutputTool
{
    /// <summary>
    /// Lista as panes disponiveis na Output Window — usado para descobrir
    /// o nome real de uma pane quando o VS2005 esta instalado em idioma
    /// diferente de en-US (ex. "Depurar" em vez de "Debug"). Ver risco
    /// anotado no HANDOFF.md.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListPanesAsync(DteSession session)
    {
        return await session.InvokeAsync(dte =>
        {
            var outputWindow = (EnvDTE.Window)dte.Windows.Item(Constants.vsWindowKindOutput).Object;
            var window = (EnvDTE.OutputWindow)outputWindow.Object;
            return (IReadOnlyList<string>)window.OutputWindowPanes.Cast<OutputWindowPane>().Select(p => p.Name).ToList();
        }).ConfigureAwait(false);
    }

    public static async Task<string> ReadWindowAsync(DteSession session, string pane)
    {
        return await session.InvokeAsync(dte =>
        {
            var outputWindow = (EnvDTE.Window)dte.Windows.Item(Constants.vsWindowKindOutput).Object;
            var window = (EnvDTE.OutputWindow)outputWindow.Object;

            foreach (OutputWindowPane candidate in window.OutputWindowPanes)
            {
                if (string.Equals(candidate.Name, pane, StringComparison.OrdinalIgnoreCase))
                {
                    var textDocument = candidate.TextDocument;
                    var start = textDocument.StartPoint.CreateEditPoint();
                    return start.GetText(textDocument.EndPoint);
                }
            }

            throw new InvalidOperationException($"Output pane '{pane}' nao encontrada. Panes disponiveis: " +
                string.Join(", ", window.OutputWindowPanes.Cast<OutputWindowPane>().Select(p => p.Name)));
        }).ConfigureAwait(false);
    }
}
