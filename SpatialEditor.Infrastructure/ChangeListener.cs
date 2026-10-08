using Npgsql;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Listens for <c>drawing_changed</c> notifications on a dedicated connection. It reconnects with a
/// growing delay after a failure, and raises <see cref="ChangesAvailable"/> (1) on every
/// notification, (2) right after each (re)connect and (3) once per safety-net interval, so a
/// notification lost while disconnected — or never delivered — is still picked up.
/// The event may fire on a background thread.
/// </summary>
public sealed class ChangeListener : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
    };

    private readonly string connectionString;
    private readonly TimeSpan safetyInterval;
    private readonly CancellationTokenSource stopping = new();
    private Task? loop;
    private Timer? safetyTimer;
    private int disposed;

    public event Action? ChangesAvailable;

    /// <summary>True when the notification connection is up, false while reconnecting.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected { get; private set; }

    public ChangeListener(string connectionString, TimeSpan? safetyInterval = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Detects a silently dropped connection (e.g. server restart) while waiting for notifications.
            KeepAlive = 30,
            Pooling = false
        };
        this.connectionString = builder.ConnectionString;
        this.safetyInterval = safetyInterval ?? TimeSpan.FromSeconds(60);
    }

    public void Start()
    {
        if (loop is not null)
        {
            return;
        }

        safetyTimer = new Timer(_ => ChangesAvailable?.Invoke(), null, safetyInterval, safetyInterval);
        loop = Task.Run(() => RunAsync(stopping.Token));
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        ConnectionStateChanged?.Invoke(connected);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                connection.Notification += (_, _) => ChangesAvailable?.Invoke();
                await using (var listen = new NpgsqlCommand($"LISTEN {ChangeFeedRepository.NotifyChannel};", connection))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                attempt = 0;
                SetConnected(true);
                ChangesAvailable?.Invoke(); // catch up on anything missed while not listening

                while (!cancellationToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                SetConnected(false);
                var delay = RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)];
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetConnected(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        stopping.Cancel();
        if (safetyTimer is not null)
        {
            await safetyTimer.DisposeAsync();
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Shutting down: nothing useful to do with a late failure.
            }
        }

        stopping.Dispose();
    }
}
