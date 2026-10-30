namespace QuietSelection.App.Speech;

/// <summary>
/// Keeps the selected voices of one worker loaded. A newer selection cancels an older preload;
/// only the latest request may update <see cref="Status"/>.
/// </summary>
internal sealed class ResidentModels(string label, Func<string[], CancellationToken, Task> configure, Action changed)
{
    private string? signature;
    private CancellationTokenSource? cancellation;
    private string[] loaded = [];
    internal Task? Running { get; private set; }
    internal string Label => label;
    internal string Status { get; private set; } = label + ": модели загружаются при выборе голосов.";
    internal IReadOnlyList<string> Loaded => loaded;
    internal bool Busy => cancellation is not null;

    internal void Update(string[] ids)
    {
        string next = string.Join("|", ids);
        if (next == signature) return;
        signature = next;
        cancellation?.Cancel();
        var current = new CancellationTokenSource(); cancellation = current;
        Running = ConfigureAsync(ids, current);
    }

    /// <summary>Stops a preload and unloads every model, for example before replacing model files.</summary>
    internal async Task ReleaseAsync(CancellationToken token)
    {
        cancellation?.Cancel();
        if (Running is not null) await Running;
        await configure([], token);
        loaded = []; signature = null;
    }

    internal void Cancel() => cancellation?.Cancel();

    private async Task ConfigureAsync(string[] ids, CancellationTokenSource current)
    {
        Status = ids.Length == 0 ? label + ": освобождение памяти…" : label + ": загрузка выбранных голосов…";
        changed();
        try
        {
            await configure(ids, current.Token);
            if (cancellation != current) return;
            loaded = ids;
            Status = ids.Length == 0 ? label + ": в памяти нет моделей." : $"{label}: в памяти голосов — {ids.Length}.";
        }
        catch (OperationCanceledException) { }
        catch (QuietException ex) { if (cancellation == current) Fail(Messages.For(ex.Code)); }
        catch { if (cancellation == current) Fail($"Не удалось загрузить {label}. Повторите выбор голоса."); }
        finally
        {
            if (cancellation == current) cancellation = null;
            current.Dispose(); changed();
        }
    }

    private void Fail(string message) { Status = message; loaded = []; signature = null; }
}
