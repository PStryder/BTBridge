using System;
using System.Threading;
using BTBridge.Bridge;
using Xunit;

namespace BTBridge.Tests
{
    /// <summary>
    /// The review's reproduction: queue a write, let it time out while the frame pump is stalled,
    /// retry, resume the pump. Before the fix both calls reported failure and both then executed.
    /// </summary>
    [Collection("MainThread")]
    public class MainThreadTests
    {
        [Fact]
        public void A_request_that_times_out_before_starting_never_runs()
        {
            MainThread.ResetForTests();
            int mutations = 0;
            Func<object> write = () => { Interlocked.Increment(ref mutations); return null; };

            var first = Assert.Throws<NotExecutedException>(() => MainThread.Run(write, 50));
            var retry = Assert.Throws<NotExecutedException>(() => MainThread.Run(write, 50));
            Assert.Contains("NOT executed", first.Message);
            Assert.Contains("safe to retry", retry.Message);

            MainThread.Pump(); // the frame pump resumes
            Assert.Equal(0, mutations);
        }

        [Fact]
        public void A_serviced_request_returns_its_result()
        {
            MainThread.ResetForTests();
            object result = null;
            var caller = new Thread(() => result = MainThread.Run(() => 42, 5000));
            caller.Start();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (caller.IsAlive && DateTime.UtcNow < deadline)
            {
                MainThread.Pump();
                Thread.Sleep(5);
            }
            caller.Join(1000);
            Assert.Equal(42, result);
            MainThread.ResetForTests();
        }

        [Fact]
        public void Work_that_started_before_the_deadline_reports_its_real_outcome()
        {
            MainThread.ResetForTests();
            int mutations = 0;
            var started = new ManualResetEvent(false);
            object result = null;
            Exception error = null;
            var caller = new Thread(() =>
            {
                try
                {
                    result = MainThread.Run(() =>
                    {
                        started.Set();
                        Thread.Sleep(200); // still running when the caller's 50 ms deadline passes
                        Interlocked.Increment(ref mutations);
                        return "done";
                    }, 50);
                }
                catch (Exception e)
                {
                    error = e;
                }
            });
            caller.Start();
            var pump = new Thread(MainThread.Pump);
            Thread.Sleep(10);
            pump.Start();
            caller.Join(5000);
            pump.Join(5000);
            Assert.Null(error);
            Assert.Equal("done", result);
            Assert.Equal(1, mutations);
            MainThread.ResetForTests();
        }
    }
}
