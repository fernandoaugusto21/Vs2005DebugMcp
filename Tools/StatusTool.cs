using Vs2005DebugMcp.Dte;

namespace Vs2005DebugMcp.Tools;

public static class StatusTool
{
    public static async Task<string> GetStatusAsync(DteSession session)
    {
        var mode = await session.GetDebugModeAsync().ConfigureAwait(false);
        return StepTool.ModeName(mode);
    }
}
