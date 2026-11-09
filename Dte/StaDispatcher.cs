using System.Collections.Concurrent;

namespace Vs2005DebugMcp.Dte;

/// <summary>
/// Fila de execucao single-thread, apartment STA, para todas as chamadas
/// COM contra o DTE. O objeto DTE e apartment-threaded: qualquer chamada
/// feita fora da thread que o criou/anexou lanca RPC_E_WRONG_THREAD (ou
/// trava). Nenhuma chamada a `dte.*` deve acontecer fora desta thread —
/// todo tool enfileira sua acao aqui e espera o resultado.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    public StaDispatcher()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "vs2005-dte-sta",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    public Task InvokeAsync(Action action)
    {
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    private void RunLoop()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable(_cts.Token))
            {
                action();
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose() pediu para parar — encerramento normal.
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
        _cts.Dispose();
    }
}
