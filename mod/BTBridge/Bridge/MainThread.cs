using System;
using System.Collections.Generic;
using System.Threading;

namespace BTBridge.Bridge
{
    /// <summary>
    /// Marshals work from the HTTP listener thread onto Unity's main thread.
    /// Game state is not thread-safe, so every read or write of it goes through here.
    /// <see cref="Pump"/> is driven by a postfix on UnityGameInstance.Update.
    ///
    /// A request that times out before the main thread picks it up is CANCELLED and never runs:
    /// the caller is told nothing happened, so a retry can't duplicate a purchase or answer the
    /// wrong prompt later (a review reproduced both calls of a timeout+retry executing once the
    /// pump resumed). One already running is waited for, and its real outcome returned.
    /// </summary>
    public static class MainThread
    {
        private const int Pending = 0, Running = 1, Cancelled = 2;

        /// <summary>How long to keep waiting once work has started (it runs within one frame).</summary>
        public static int RunningGraceMs = 30000;

        private sealed class WorkItem
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
            public int State = Pending;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private static readonly Queue<WorkItem> Queue = new Queue<WorkItem>();
        private static readonly object Sync = new object();

        public static int ManagedThreadId { get; private set; } = -1;

        public static object Run(Func<object> work, int timeoutMs)
        {
            if (Thread.CurrentThread.ManagedThreadId == ManagedThreadId)
            {
                return work();
            }
            var item = new WorkItem { Work = work };
            lock (Sync)
            {
                Queue.Enqueue(item);
            }
            if (!item.Done.WaitOne(timeoutMs))
            {
                if (Interlocked.CompareExchange(ref item.State, Cancelled, Pending) == Pending)
                {
                    throw new NotExecutedException(
                        $"main thread did not pick up the request within {timeoutMs} ms (game paused, loading, or minimized?); " +
                        "it was cancelled and NOT executed, so it is safe to retry");
                }
                // It started just as we gave up: its effects are happening, so report them.
                if (!item.Done.WaitOne(RunningGraceMs))
                {
                    throw new OutcomeUnknownException(
                        "the request started on the main thread but has not finished; its outcome is unknown. " +
                        "Read the state before retrying");
                }
            }
            if (item.Error != null)
            {
                throw item.Error;
            }
            return item.Result;
        }

        public static void Pump()
        {
            ManagedThreadId = Thread.CurrentThread.ManagedThreadId;
            while (true)
            {
                WorkItem item;
                lock (Sync)
                {
                    if (Queue.Count == 0)
                    {
                        return;
                    }
                    item = Queue.Dequeue();
                }
                if (Interlocked.CompareExchange(ref item.State, Running, Pending) != Pending)
                {
                    continue; // cancelled by its caller's timeout: never run it
                }
                try
                {
                    item.Result = item.Work();
                }
                catch (Exception e)
                {
                    item.Error = e;
                }
                item.Done.Set();
            }
        }

        /// <summary>For tests: forget the pump thread so Run always queues.</summary>
        public static void ResetForTests()
        {
            ManagedThreadId = -1;
            lock (Sync)
            {
                Queue.Clear();
            }
        }
    }

    /// <summary>Timed out before starting; cancelled, nothing happened. Safe to retry.</summary>
    public sealed class NotExecutedException : TimeoutException
    {
        public NotExecutedException(string message) : base(message)
        {
        }
    }

    /// <summary>Started but didn't finish in time; it may or may not have taken effect.</summary>
    public sealed class OutcomeUnknownException : TimeoutException
    {
        public OutcomeUnknownException(string message) : base(message)
        {
        }
    }
}
