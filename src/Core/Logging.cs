using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace ReversalConfirmation.Core
{
    public interface ILogSink
    {
        void Write(LogRow row);
    }

    /// <summary>
    /// Non-blocking CSV writer: rows are queued and written by a background thread, so the chart
    /// thread never waits for the disk. The file is rewritten on every full recalculation.
    /// </summary>
    public sealed class CsvLogWriter : ILogSink, IDisposable
    {
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 200_000);
        private readonly Thread _thread;
        private readonly string _path;
        private readonly bool _blockWhenFull;
        private volatile bool _failed;
        private long _dropped;

        public string Path => _path;
        public string Error { get; private set; }
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <param name="blockWhenFull">false (indicator): never block, drop rows if the disk lags; true (back-test): wait, lose nothing.</param>
        public CsvLogWriter(string path, bool blockWhenFull = false)
        {
            _path = path;
            _blockWhenFull = blockWhenFull;
            _thread = new Thread(Run) { IsBackground = true, Name = "ReversalConfirmation CSV", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Write(LogRow row)
        {
            if (_failed || _queue.IsAddingCompleted) return;
            if (_blockWhenFull) _queue.Add(row.ToCsv());
            else if (!_queue.TryAdd(row.ToCsv())) Interlocked.Increment(ref _dropped);   // never block the chart thread
        }

        private void Run()
        {
            StreamWriter w = null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                w = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 1 << 16);
                w.WriteLine(LogRow.Header());
                var last = DateTime.UtcNow;
                while (!_queue.IsCompleted)
                {
                    if (_queue.TryTake(out var line, 500))
                    {
                        w.WriteLine(line);
                        if ((DateTime.UtcNow - last).TotalSeconds > 2)
                        {
                            w.Flush();
                            last = DateTime.UtcNow;
                        }
                    }
                    else w.Flush();
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Error = e.Message;
            }
            finally
            {
                try { w?.Flush(); w?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            try
            {
                _queue.CompleteAdding();
                _thread.Join(3000);
            }
            catch { }
        }
    }

    /// <summary>In-memory sink used by tests and the replay tool.</summary>
    public sealed class MemorySink : ILogSink
    {
        public readonly System.Collections.Generic.List<LogRow> Rows = new System.Collections.Generic.List<LogRow>();
        public void Write(LogRow row) => Rows.Add(row);
    }
}
