using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

internal sealed class PresentationLogWriter : IDisposable
{
    private const int MaximumQueuedRows = 65_536;
    private readonly BlockingCollection<string> rows = new(new ConcurrentQueue<string>(), MaximumQueuedRows);
    private readonly Task writerTask;
    private int disposed;
    private long droppedRowCount;
    private string? errorMessage;

    private PresentationLogWriter(string filePath)
    {
        FilePath = filePath;
        writerTask = Task.Run(WriteRows);
    }

    public string FilePath { get; }

    public string? ErrorMessage => Volatile.Read(ref errorMessage);

    public long DroppedRowCount => Interlocked.Read(ref droppedRowCount);

    public static PresentationLogWriter? TryCreate(out string? error)
    {
        error = null;
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new IOException("Windows did not provide a local app data directory.");
            }

            var logDirectory = Path.Combine(localAppData, "SMW2PC", "logs");
            Directory.CreateDirectory(logDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var filePath = Path.Combine(logDirectory, $"frame-pacing-{timestamp}-{uniqueSuffix}.csv");
            return new PresentationLogWriter(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = exception.Message;
            return null;
        }
    }

    public bool TryWrite(string row)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        try
        {
            if (rows.TryAdd(row))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // The app is shutting down while a final presentation is being logged.
        }

        Interlocked.Increment(ref droppedRowCount);
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        rows.CompleteAdding();
        try
        {
            writerTask.Wait();
        }
        catch (AggregateException)
        {
            // WriteRows records file errors and keeps shutdown from failing.
        }
        finally
        {
            rows.Dispose();
        }
    }

    private void WriteRows()
    {
        try
        {
            using var stream = new FileStream(
                FilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
            var lastFlushTimestamp = Stopwatch.GetTimestamp();
            foreach (var row in rows.GetConsumingEnumerable())
            {
                writer.WriteLine(row);
                var currentTimestamp = Stopwatch.GetTimestamp();
                if (currentTimestamp - lastFlushTimestamp >= Stopwatch.Frequency)
                {
                    writer.Flush();
                    lastFlushTimestamp = currentTimestamp;
                }
            }

            writer.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Volatile.Write(ref errorMessage, exception.Message);
        }
    }
}
