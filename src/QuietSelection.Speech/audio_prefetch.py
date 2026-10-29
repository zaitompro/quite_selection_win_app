"""One PCM fragment of lookahead; no files, no parallel model invocations."""
import queue
import threading


def prefetch(source, cancellation):
    """Compute the next fragment while the caller plays the current one.

    A permit is acquired *before* advancing the source, so at most one next
    fragment is computed or queued. Closing joins the producer before a worker
    can acknowledge cancellation or reuse its model.
    """
    items = queue.Queue(maxsize=1)
    permit = threading.Semaphore(1)
    stopped = threading.Event()

    def produce():
        iterator = iter(source)
        try:
            while not stopped.is_set():
                if not permit.acquire(timeout=0.02):
                    continue
                if stopped.is_set():
                    return
                cancellation.check()
                try:
                    item = next(iterator)
                except StopIteration:
                    items.put(('done', None))
                    return
                items.put(('audio', item))
        except Exception as error:
            # The acquired permit guarantees an empty queue, even on failure.
            items.put(('error', error))
        finally:
            iterator.close()

    producer = threading.Thread(target=produce, name='speech-prefetch', daemon=True)
    producer.start()
    completed = False
    try:
        while True:
            cancellation.check()
            try:
                kind, value = items.get(timeout=0.02)
            except queue.Empty:
                continue
            if kind == 'done':
                completed = True
                return
            if kind == 'error':
                completed = True
                raise value
            permit.release()
            yield value
    finally:
        stopped.set()
        if not completed:
            cancellation.cancelled.set()
            options = cancellation.options
            if options is not None:
                options.terminate = True
        producer.join()
