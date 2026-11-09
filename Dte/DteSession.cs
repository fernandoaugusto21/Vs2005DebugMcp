using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;

namespace Vs2005DebugMcp.Dte;

public sealed class DteConnectionException(string message) : Exception(message);

/// <summary>
/// <see cref="Marshal.GetActiveObject(string)"/> nao existe em .NET
/// moderno (so em .NET Framework) — reimplementado aqui via P/Invoke
/// direto contra ole32/oleaut32, que e o que aquele metodo fazia
/// internamente.
/// </summary>
internal static class RunningObjectTable
{
    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, nint pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    public static object GetActiveObject(string progId)
    {
        var hr = CLSIDFromProgID(progId, out var clsid);
        Marshal.ThrowExceptionForHR(hr);

        GetActiveObject(ref clsid, 0, out var instance);
        return instance;
    }
}

/// <summary>
/// Mantem a referencia viva ao DTE do VS2005 e garante que toda chamada
/// COM passe pelo <see cref="StaDispatcher"/>. Uma unica instancia serve
/// o processo MCP inteiro (o VS2005 alvo e sempre a mesma instancia
/// anexada em "Connect").
/// </summary>
public sealed class DteSession : IDisposable
{
    private const string ProgId = "VisualStudio.DTE.8.0";

    private readonly StaDispatcher _dispatcher = new();
    private DTE2? _dte;

    /// <summary>
    /// Tenta anexar a uma instancia do VS2005 ja aberta via ROT
    /// (Running Object Table). Nao cria uma instancia nova por padrao —
    /// abrir um devenv.exe novo nao serve para anexar a uma sessao de
    /// debug ja em andamento.
    /// </summary>
    public async Task ConnectAsync(bool allowCreateNewInstance = false)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var activeObject = RunningObjectTable.GetActiveObject(ProgId);
                _dte = (DTE2)activeObject;
                return;
            }
            catch (COMException ex) when (allowCreateNewInstance)
            {
                // Cai para CreateObject soh se explicitamente pedido.
                _ = ex;
            }
            catch (COMException ex)
            {
                throw new DteConnectionException(
                    $"Nao foi possivel anexar a uma instancia do VS2005 via ROT (ProgID '{ProgId}'). " +
                    "Causa mais provavel: elevacao assimetrica — o VS2005 e este processo MCP " +
                    "server precisam rodar no MESMO nivel de UAC (ambos elevados ou ambos nao-elevados) " +
                    "para se enxergarem na Running Object Table. Verifique tambem se o devenv.exe " +
                    "esta de fato aberto nesta mesma sessao de usuario. " +
                    $"Erro COM original: {ex.Message} (0x{ex.HResult:X8})");
            }

            var type = Type.GetTypeFromProgID(ProgId)
                ?? throw new DteConnectionException($"ProgID '{ProgId}' nao registrado nesta maquina — VS2005 esta instalado?");
            var created = Activator.CreateInstance(type)
                ?? throw new DteConnectionException($"CreateObject('{ProgId}') retornou null.");
            _dte = (DTE2)created;
        }).ConfigureAwait(false);
    }

    public Task<T> InvokeAsync<T>(Func<DTE2, T> action)
    {
        return _dispatcher.InvokeAsync(() =>
        {
            if (_dte is null)
            {
                throw new DteConnectionException("DteSession nao conectada — chame ConnectAsync() antes.");
            }
            return action(_dte);
        });
    }

    public Task InvokeAsync(Action<DTE2> action)
    {
        return InvokeAsync<object?>(dte =>
        {
            action(dte);
            return null;
        });
    }

    /// <summary>
    /// Modo atual do debugger. Chamada barata usada como guarda antes de
    /// qualquer tool de inspecao (Locals/Evaluate/CallStack exigem
    /// dbgBreakMode).
    /// </summary>
    public Task<dbgDebugMode> GetDebugModeAsync() =>
        InvokeAsync(dte => dte.Debugger.CurrentMode);

    public void Dispose()
    {
        _dispatcher.Dispose();
    }
}
