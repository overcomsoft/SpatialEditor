using System.Windows.Threading;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>
/// Keeps track of changes other sessions saved to the layers this window has loaded. It owns the
/// notification listener and the "baseline" (the last change number whose data is on screen); the
/// window decides when to ask the user and how to refresh.
/// </summary>
internal sealed class SyncCoordinator : IAsyncDisposable
{
    // Several saves in a row (or the connect + notification pair) are folded into one check.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly ChangeFeedRepository feed;
    private readonly ChangeListener listener;
    private readonly Guid sessionId;
    private readonly Func<IReadOnlyCollection<string>?> loadedLayers;
    private readonly DispatcherTimer debounceTimer;
    private bool disposed;

    /// <summary>Last change number reflected on screen.</summary>
    public long Baseline { get; private set; }

    /// <summary>The user answered "no" up to this change number; only newer changes ask again.</summary>
    public long DeclinedUpTo { get; private set; }

    /// <summary>Unseen changes found by the last check (null when the screen is up to date).</summary>
    public ChangeBatch? Pending { get; private set; }

    public bool IsListening => listener.IsConnected;

    /// <summary>Raised on the UI thread after a debounce when a check is worth doing.</summary>
    public event Action? CheckRequested;

    /// <summary>Raised on the UI thread when the notification connection goes up or down.</summary>
    public event Action<bool>? ConnectionChanged;

    public SyncCoordinator(string connectionString, Guid sessionId, Func<IReadOnlyCollection<string>?> loadedLayers)
    {
        this.sessionId = sessionId;
        this.loadedLayers = loadedLayers;
        feed = new ChangeFeedRepository(connectionString);
        listener = new ChangeListener(connectionString);
        debounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            if (!disposed)
            {
                CheckRequested?.Invoke();
            }
        };
        listener.ChangesAvailable += () => Dispatch(() =>
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        });
        listener.ConnectionStateChanged += connected => Dispatch(() => ConnectionChanged?.Invoke(connected));
    }

    private static void Dispatch(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    public async Task StartAsync()
    {
        await feed.EnsureSchemaAsync();
        Baseline = await feed.GetLatestSeqAsync();
        listener.Start();
        // Housekeeping: old log rows are of no use to anyone who is online.
        _ = feed.PurgeOldAsync().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Call right before (re)loading layers: whatever is saved from here on counts as new.</summary>
    public async Task ResetBaselineAsync()
    {
        Baseline = await feed.GetLatestSeqAsync();
        DeclinedUpTo = 0;
        Pending = null;
    }

    /// <summary>Looks for changes by other sessions on the loaded layers since the baseline.</summary>
    public async Task<ChangeBatch> CheckAsync()
    {
        var batch = await feed.GetChangesSinceAsync(Baseline, sessionId, loadedLayers());
        if (batch.HasChanges)
        {
            Pending = batch;
        }
        else
        {
            // Nothing relevant (own saves, other layers): the screen is current up to here.
            Baseline = batch.LatestSeq;
            Pending = null;
        }

        return batch;
    }

    public bool IsNewSinceDeclined(ChangeBatch batch) => batch.LatestSeq > DeclinedUpTo;

    public void Decline(ChangeBatch batch) => DeclinedUpTo = batch.LatestSeq;

    public static int CountChanges(ChangeBatch batch) =>
        batch.Summaries.Sum(summary => summary.WholeLayer ? Math.Max(1, summary.Added + summary.Updated + summary.Deleted) : summary.Added + summary.Updated + summary.Deleted);

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        debounceTimer.Stop();
        await listener.DisposeAsync();
        await feed.DisposeAsync();
    }
}
