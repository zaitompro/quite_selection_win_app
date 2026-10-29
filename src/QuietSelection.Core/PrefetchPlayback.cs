using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace QuietSelection.Core;

/// <summary>One playback consumer; at most one queued item ahead of it.</summary>
public static class PrefetchPlayback
{
    public static async Task RunAsync<T>(
        Func<Func<T, CancellationToken, ValueTask>, CancellationToken, Task> produce,
        Func<T, CancellationToken, Task> play, Action<T> release, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Exception? productionError = null, playbackError = null;
        async ValueTask Send(T item, CancellationToken cancellation)
        {
            try { await queue.Writer.WriteAsync(item, cancellation); }
            catch { release(item); throw; }
        }
        async Task Produce()
        {
            try { await produce(Send, stop.Token); }
            catch (Exception ex) { productionError = ex; stop.Cancel(); }
            finally { queue.Writer.TryComplete(); }
        }
        var producer = Produce();
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token))
                try { stop.Token.ThrowIfCancellationRequested(); await play(item, stop.Token); }
                finally { release(item); }
        }
        catch (Exception ex) { playbackError = ex; }
        finally
        {
            stop.Cancel();
            await producer;
            while (queue.Reader.TryRead(out var item)) release(item);
        }
        if (productionError is not null && (productionError is not OperationCanceledException || playbackError is null))
            ExceptionDispatchInfo.Capture(productionError).Throw();
        if (playbackError is not null) ExceptionDispatchInfo.Capture(playbackError).Throw();
        token.ThrowIfCancellationRequested();
    }
}
