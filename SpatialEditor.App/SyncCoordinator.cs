using System.Windows.Threading;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// SyncCoordinator : 다른 세션이 저장한 변경을 감지해 화면 갱신 여부를 판단하는 조정자
// ----------------------------------------------------------------------------
// 흐름도
//   StartAsync: 변경 피드 스키마 보장 → Baseline = 최신 변경 번호 → 알림 리스너 시작
//               → 오래된 로그 비동기 정리(PurgeOldAsync, 실패 무시)
//   다른 세션이 저장 → DB 알림 → listener.ChangesAvailable → UI 스레드(Dispatch)
//     → 디바운스 타이머(2초) 재시작 → 연속 알림을 하나로 합쳐 CheckRequested 이벤트 발생
//   창(MainWindow)이 CheckRequested 를 받아 CheckAsync 호출
//     → 피드에서 Baseline 이후, 내 세션 제외, 불러온 레이어만 조회
//        · 변경 있음 → Pending 에 보관(창이 사용자에게 묻고 새로 고칠지 결정)
//        · 변경 없음(내 저장/다른 레이어) → Baseline 을 최신으로 올림
//   사용자 응답: 거절하면 Decline(DeclinedUpTo 기록), 이후 더 새 변경(IsNewSinceDeclined)일 때만 다시 묻기
//   다시 불러오기 직전 ResetBaselineAsync: 이 시점 이후 저장분만 새 변경으로 취급
//   ConnectionChanged: 알림 연결 상태 변화를 UI 스레드로 전달
//   DisposeAsync: 타이머 정지 → 리스너/피드 해제
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 이 창이 불러온 레이어에 대해 다른 세션이 저장한 변경을 추적합니다. 알림 리스너와 '기준선'(화면에 반영된
/// 마지막 변경 번호)을 소유하며, 사용자에게 묻는 시점과 새로 고치는 방법은 창이 결정합니다.
/// </summary>
internal sealed class SyncCoordinator : IAsyncDisposable
{
    // 연속된 여러 번의 저장(또는 연결 직후의 연결+알림 쌍)을 한 번의 확인으로 묶습니다.
    /// <summary>연속 저장(또는 연결 직후 알림 쌍)을 한 번의 확인으로 묶기 위한 대기 시간(2초).</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>DB 변경 이력(피드) 저장소: 변경 번호 조회/정리/스키마 보장.</summary>
    private readonly ChangeFeedRepository feed;
    /// <summary>DB 알림(LISTEN/NOTIFY)을 수신하는 리스너.</summary>
    private readonly ChangeListener listener;
    /// <summary>이 프로그램 세션의 고유 ID. 내가 저장한 변경을 다른 세션의 변경과 구분(제외)하는 데 사용.</summary>
    private readonly Guid sessionId;
    /// <summary>현재 불러온 레이어 이름들을 돌려주는 함수(null 이면 전체/제한 없음으로 해석). 확인할 때마다 최신 값을 얻기 위해 함수로 받음.</summary>
    private readonly Func<IReadOnlyCollection<string>?> loadedLayers;
    /// <summary>현재 활성 배치(레이아웃) ID 를 돌려주는 함수. 해당 배치의 변경만 조회.</summary>
    private readonly Func<long> layoutId;
    /// <summary>알림을 합치는 UI 스레드 타이머(Background 우선순위).</summary>
    private readonly DispatcherTimer debounceTimer;
    /// <summary>해제 여부. true 이면 늦게 도착한 타이머 틱이 이벤트를 발생시키지 않음.</summary>
    private bool disposed;

    /// <summary>화면에 반영된 마지막 변경 번호(이 번호 이후의 변경이 '새 변경').</summary>
    public long Baseline { get; private set; }

    /// <summary>사용자가 "아니오"라고 답한 변경 번호. 이 번호를 넘는 더 새로운 변경만 다시 묻습니다.</summary>
    public long DeclinedUpTo { get; private set; }

    /// <summary>마지막 확인에서 발견된 아직 보지 않은 변경(화면이 최신이면 null).</summary>
    public ChangeBatch? Pending { get; private set; }

    /// <summary>알림 연결이 살아 있는지 여부.</summary>
    public bool IsListening => listener.IsConnected;

    /// <summary>디바운스 후 확인할 가치가 있을 때 UI 스레드에서 발생합니다(창이 <see cref="CheckAsync"/> 를 호출).</summary>
    public event Action? CheckRequested;

    /// <summary>알림 연결이 연결/끊김으로 바뀔 때 UI 스레드에서 발생합니다(인수: 연결 여부).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>조정자를 만들고 리스너 이벤트를 연결합니다(아직 시작하지 않음. <see cref="StartAsync"/> 필요).</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열.</param>
    /// <param name="sessionId">이 세션의 고유 ID(내 변경 제외용).</param>
    /// <param name="loadedLayers">불러온 레이어 이름 목록 제공 함수. null 반환 가능.</param>
    /// <param name="layoutId">활성 배치 ID 제공 함수.</param>
    public SyncCoordinator(string connectionString, Guid sessionId, Func<IReadOnlyCollection<string>?> loadedLayers, Func<long> layoutId)
    {
        this.layoutId = layoutId;
        this.sessionId = sessionId;
        this.loadedLayers = loadedLayers;
        feed = new ChangeFeedRepository(connectionString);
        listener = new ChangeListener(connectionString);
        // 타이머 틱: 한 번만 울리도록 멈춘 뒤, 해제되지 않았다면 확인 요청 이벤트를 발생시킵니다.
        debounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            if (!disposed)
            {
                CheckRequested?.Invoke();
            }
        };
        // 변경 알림이 올 때마다 타이머를 재시작해(Stop→Start) 연속 알림을 마지막 알림 2초 뒤 한 번으로 합칩니다. 리스너 스레드에서 오므로 UI 스레드로 넘깁니다.
        listener.ChangesAvailable += () => Dispatch(() =>
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        });
        listener.ConnectionStateChanged += connected => Dispatch(() => ConnectionChanged?.Invoke(connected));
    }

    /// <summary>동작을 UI 스레드(Dispatcher)에서 비동기로 실행하도록 예약합니다. 애플리케이션이 이미 종료되었으면 무시.</summary>
    /// <param name="action">UI 스레드에서 실행할 동작.</param>
    private static void Dispatch(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    /// <summary>동기화를 시작합니다: 스키마 보장, 기준선 설정, 알림 수신 시작, 오래된 로그 정리 예약.</summary>
    public async Task StartAsync()
    {
        await feed.EnsureSchemaAsync();
        Baseline = await feed.GetLatestSeqAsync();
        listener.Start();
        // 정리 작업: 온라인 사용자에게는 오래된 로그 행이 필요 없습니다. 결과를 기다리지 않고 실패도 무시합니다.
        _ = feed.PurgeOldAsync().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>레이어를 (다시) 불러오기 직전에 호출합니다: 지금부터 저장되는 것만 새 변경으로 치며, 거절/대기 상태도 초기화합니다.</summary>
    public async Task ResetBaselineAsync()
    {
        Baseline = await feed.GetLatestSeqAsync();
        DeclinedUpTo = 0;
        Pending = null;
    }

    /// <summary>기준선 이후 다른 세션이 불러온 레이어에 저장한 변경이 있는지 조회합니다.</summary>
    /// <returns>조회한 변경 묶음. 변경이 있으면 <see cref="Pending"/> 에 보관됩니다.</returns>
    public async Task<ChangeBatch> CheckAsync()
    {
        var batch = await feed.GetChangesSinceAsync(Baseline, sessionId, loadedLayers(), default, layoutId());
        if (batch.HasChanges)
        {
            Pending = batch;
        }
        else
        {
            // 관련 변경이 없으면(내 저장, 다른 레이어) 화면은 여기까지 최신이므로 기준선을 올립니다.
            Baseline = batch.LatestSeq;
            Pending = null;
        }

        return batch;
    }

    /// <summary>사용자가 이전에 거절한 이후의 새로운 변경인지 확인합니다.</summary>
    /// <param name="batch">조회한 변경 묶음.</param>
    /// <returns>묶음의 최신 번호가 거절 기록보다 크면 true(다시 물어야 함).</returns>
    public bool IsNewSinceDeclined(ChangeBatch batch) => batch.LatestSeq > DeclinedUpTo;

    /// <summary>사용자가 새로 고침을 거절했음을 기록합니다(이 묶음의 최신 번호까지는 다시 묻지 않음).</summary>
    /// <param name="batch">거절한 변경 묶음.</param>
    public void Decline(ChangeBatch batch) => DeclinedUpTo = batch.LatestSeq;

    /// <summary>변경 묶음의 표시용 변경 건수를 셉니다. 레이어 전체 변경(WholeLayer)은 세부 합이 0 이어도 최소 1건으로 칩니다.</summary>
    /// <param name="batch">변경 묶음.</param>
    /// <returns>추가+수정+삭제 합계(레이어 단위 변경 포함).</returns>
    public static int CountChanges(ChangeBatch batch) =>
        batch.Summaries.Sum(summary => summary.WholeLayer ? Math.Max(1, summary.Added + summary.Updated + summary.Deleted) : summary.Added + summary.Updated + summary.Deleted);

    /// <summary>타이머를 멈추고 알림 리스너와 변경 피드 저장소를 해제합니다.</summary>
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        debounceTimer.Stop();
        await listener.DisposeAsync();
        await feed.DisposeAsync();
    }
}
