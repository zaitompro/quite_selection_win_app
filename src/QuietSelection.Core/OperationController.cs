namespace QuietSelection.Core;

/// <summary>A cancelled operation retains its slot until its worker has actually exited.</summary>
public sealed class OperationController
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private long generation;
    public OperationState State { get; private set; }
    public bool IsBusy { get { lock (gate) return cancellation is not null; } }
    public event Action<OperationState, ErrorCode>? Changed;

    public async Task RunOrStopAsync(Func<OperationContext, Task> run)
    {
        CancellationTokenSource source;
        long id;
        lock (gate)
        {
            if (cancellation is not null) { cancellation.Cancel(); return; }
            source = cancellation = new();
            id = ++generation;
        }
        var error = ErrorCode.None;
        try { await run(new(this, id, source.Token)); }
        catch (OperationCanceledException) { error = ErrorCode.Cancelled; }
        catch (QuietException ex) { error = ex.Code; }
        catch { error = ErrorCode.WorkerFailure; }
        finally
        {
            lock (gate)
            {
                if (source.IsCancellationRequested) error = ErrorCode.Cancelled;
                cancellation = null;
                State = OperationState.Idle;
                source.Dispose();
                Changed?.Invoke(State, error);
            }
        }
    }

    public void Stop() { lock (gate) cancellation?.Cancel(); }

    internal void SetState(long id, OperationState state, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (id != generation || cancellation is null) throw new OperationCanceledException(token);
            State = state;
            Changed?.Invoke(state, ErrorCode.None);
        }
    }
}

public sealed class OperationContext
{
    private readonly OperationController owner;
    private readonly long id;
    internal OperationContext(OperationController owner, long id, CancellationToken token)
    { this.owner = owner; this.id = id; Token = token; }
    public CancellationToken Token { get; }
    public void Transition(OperationState state) => owner.SetState(id, state, Token);
    public void EnsureCurrent() => Token.ThrowIfCancellationRequested();
}
