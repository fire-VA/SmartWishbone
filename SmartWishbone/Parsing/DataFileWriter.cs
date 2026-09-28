using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace SmartWishbone
{
    // writes the data file on one background thread, in the order the saves were made, so adding or removing a
    // trackable doesn't hold the server's frame for the disk; drained when the game quits so the last save lands
    internal static class DataFileWriter
    {
        private const int drainTimeoutMs = 5000;
        private const string threadName = "SmartWishbone.DataFile";

        private static readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
        private static readonly object startLock = new object();
        private static Thread thread;

        // BepInEx only hears Unity log lines from the main thread, so a failed write is kept here and reported from the
        // main thread at the next save or at quit
        private static string lastFailure;

        internal static void Enqueue(string path, string text)
        {
            ReportFailure();

            Action job = () => File.WriteAllText(path, text);

            EnsureThread();

            try
            {
                jobs.Add(job);
            }
            catch (InvalidOperationException)
            {
                Run(job);
            }
        }

        internal static void Drain()
        {
            if (thread == null)
            {
                return;
            }

            var done = new ManualResetEventSlim(false);

            try
            {
                jobs.Add(() => done.Set());
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!done.Wait(drainTimeoutMs))
            {
                Helper.LogWarning($"Data file still saving after {drainTimeoutMs / 1000} s at shutdown.");
            }

            ReportFailure();
        }

        private static void ReportFailure()
        {
            string failure = Interlocked.Exchange(ref lastFailure, null);

            if (failure != null)
            {
                Helper.LogWarning(failure);
            }
        }

        private static void EnsureThread()
        {
            lock (startLock)
            {
                if (thread != null)
                {
                    return;
                }

                thread = new Thread(Loop) { IsBackground = true, Name = threadName };
                thread.Start();
            }
        }

        private static void Loop()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                Run(job);
            }
        }

        private static void Run(Action job)
        {
            try
            {
                job();
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref lastFailure, $"Failed saving the data file in the background: {e.Message}");
            }
        }
    }
}
