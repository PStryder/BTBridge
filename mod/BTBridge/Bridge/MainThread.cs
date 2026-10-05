using System;
using System.Collections.Generic;
using System.Threading;

namespace BTBridge.Bridge
{
    /// <summary>
    /// Marshals work from the HTTP listener thread onto Unity's main thread.
    /// Game state is not thread-safe, so every read or write of it goes through here.
    /// <see cref="Pump"/> is driven by a postfix on UnityGameInstance.Update.
    /// </summary>
    public static class MainThread
    {
        private sealed class WorkItem
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
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
                // The item stays queued and will still run; its result is just discarded.
                throw new TimeoutException($"main thread did not service the request within {timeoutMs} ms (game paused, loading, or minimized?)");
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
    }
}
