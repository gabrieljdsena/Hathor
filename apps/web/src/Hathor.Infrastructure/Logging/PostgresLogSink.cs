using System.Threading.Channels;
using Dapper;
using Npgsql;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Hathor.Infrastructure.Logging;

// Serilog sink that persists Error/Fatal events to the shared lab Postgres
// `logs` table (application='hathor', same table myhomelab reads on /logs).
// Async, bounded and self-swallowing: a down logs database can never break
// the path that is trying to report the error.
public sealed class PostgresLogSink : ILogEventSink, IDisposable
{
    private const string Application = "hathor";
    private const int BatchSize = 50;
    private const int QueueCapacity = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private const string EnsureTableSql = """
        CREATE TABLE IF NOT EXISTS logs (
            id          INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            application VARCHAR(255) NOT NULL,
            log         TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_logs_application ON logs (application);
        CREATE INDEX IF NOT EXISTS ix_logs_id_desc ON logs (id DESC);
        """;

    private readonly NpgsqlDataSource? _dataSource;
    private readonly Channel<string> _queue;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _writeBatch;
    private readonly TimeSpan _flushInterval;
    private readonly Task _flusher;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public PostgresLogSink(string? connectionString)
        : this(CreateQueue(), CreateWriter(connectionString, out var dataSource), FlushInterval)
    {
        _dataSource = dataSource;
    }

    internal PostgresLogSink(
        Channel<string> queue,
        Func<IReadOnlyList<string>, CancellationToken, Task> writeBatch,
        TimeSpan flushInterval)
    {
        _queue = queue;
        _writeBatch = writeBatch;
        _flushInterval = flushInterval;
        _flusher = Task.Run(FlushLoopAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed) return;
        var text = FormatMessage(
            logEvent.Timestamp, logEvent.Level.ToString(),
            logEvent.RenderMessage(), logEvent.Exception?.ToString());

        // Never block or throw from the logging pipeline. When the queue is full (Postgres
        // is down and errors are piling up) the newest entry is dropped — the console/file
        // sinks still have it, and a SelfLog note records the drop.
        if (!_queue.Writer.TryWrite(text))
        {
            SelfLog.WriteLine("PostgresLogSink queue full; dropping error log.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.Complete();
        _shutdown.Cancel();

        try
        {
            // Bound the drain: flush what we can, then give up rather than hang shutdown.
            if (!_flusher.Wait(DisposeTimeout))
            {
                SelfLog.WriteLine("PostgresLogSink timed out flushing pending logs on shutdown.");
            }
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("PostgresLogSink failed during shutdown flush: {0}", ex.Message);
        }

        if (_flusher.IsFaulted)
        {
            SelfLog.WriteLine("PostgresLogSink flusher faulted: {0}", _flusher.Exception?.Message);
        }

        _dataSource?.Dispose();
        _shutdown.Dispose();
    }

    private async Task FlushLoopAsync()
    {
        var batch = new List<string>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token))
            {
                batch.Clear();
                Drain(batch);

                if (batch.Count < BatchSize)
                {
                    // Coalesce bursts so one INSERT covers them.
                    try
                    {
                        await Task.Delay(_flushInterval, _shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down: fall through and flush what we have.
                    }

                    Drain(batch);
                }

                await FlushBatchAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Idle at shutdown: fall through to the final drain below.
        }

        // Final drain: the wait above can throw (or report completion) while items remain,
        // so sweep the queue once more before exiting.
        batch.Clear();
        while (_queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count >= BatchSize)
            {
                await FlushBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch);
        }
    }

    private void Drain(List<string> batch)
    {
        while (batch.Count < BatchSize && _queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }
    }

    private async Task FlushBatchAsync(List<string> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await _writeBatch(batch, timeout.Token);
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("PostgresLogSink failed to persist {0} log(s): {1}", batch.Count, ex.Message);
        }
    }

    private static Channel<string> CreateQueue() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private sealed class WriterState
    {
        public int TableEnsured;
    }

    private static Func<IReadOnlyList<string>, CancellationToken, Task> CreateWriter(
        string? connectionString, out NpgsqlDataSource? dataSource)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Logs database not configured: console/file sinks still record
            // everything; the PG sink stays a harmless no-op.
            dataSource = null;
            return (_, _) => Task.CompletedTask;
        }

        dataSource = NpgsqlDataSource.Create(connectionString);
        var ds = dataSource;
        var state = new WriterState();
        return (batch, ct) => WriteBatchAsync(ds, state, batch, ct);
    }

    private static async Task WriteBatchAsync(
        NpgsqlDataSource dataSource, WriterState state,
        IReadOnlyList<string> batch, CancellationToken cancellationToken)
    {
        // The logs table belongs to the lab database (myhomelab owns its
        // migrations); make sure it exists so Hathor works standalone too.
        if (Interlocked.CompareExchange(ref state.TableEnsured, 1, 0) == 0)
        {
            try
            {
                await using var ensure = await dataSource.OpenConnectionAsync(cancellationToken);
                await ensure.ExecuteAsync(new CommandDefinition(
                    EnsureTableSql, cancellationToken: cancellationToken,
                    commandTimeout: (int)WriteTimeout.TotalSeconds));
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref state.TableEnsured, 0);
                throw new InvalidOperationException("Ensuring logs table failed.", ex);
            }
        }

        var parameters = new DynamicParameters();
        parameters.Add("Application", Application);
        for (var i = 0; i < batch.Count; i++)
        {
            parameters.Add($"Log{i}", batch[i]);
        }

        var values = string.Join(", ", Enumerable.Range(0, batch.Count).Select(i => $"(@Application, @Log{i})"));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO logs (application, log) VALUES {values};",
            parameters,
            cancellationToken: cancellationToken,
            commandTimeout: (int)WriteTimeout.TotalSeconds));
    }

    // Local wall time with its UTC offset (e.g. "2026-10-04 11:08:07 -03:00"):
    // matches the machine clock at a glance while staying unambiguous.
    // (Serilog's :u format is UTC, which reads 3h ahead on a UTC-3 box.)
    internal static string FormatMessage(
        DateTimeOffset timestamp, string level, string message, string? exception) =>
        exception is null
            ? $"{timestamp:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}"
            : $"{timestamp:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}{Environment.NewLine}{exception}";
}
