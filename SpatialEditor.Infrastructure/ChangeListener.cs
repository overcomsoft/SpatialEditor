// ============================================================================
// ChangeListener 전체 흐름도
//
//   생성자 → 연결 문자열에 KeepAlive=30, Pooling=false 적용(전용 연결)
//   Start()
//     ├─ safetyTimer 시작 (safetyInterval마다 ChangesAvailable 발생: 안전망)
//     └─ RunAsync 백그라운드 루프
//          연결 열기 → Notification 핸들러 등록 → LISTEN drawing_changed
//          → attempt 초기화, SetConnected(true) → ChangesAvailable 1회(누락분 따라잡기)
//          → connection.WaitAsync 반복(알림 수신 시 핸들러가 ChangesAvailable 발생)
//          → 예외 발생 시 SetConnected(false) → 재시도 지연(1,2,5,10,30초; 마지막 값 유지) → 재연결
//   DisposeAsync() → 취소 요청 → 타이머 해제 → 루프 종료 대기(최대 3초) → CancellationTokenSource 해제
//
// 외부 의존: PostgreSQL LISTEN/NOTIFY 채널(ChangeFeedRepository.NotifyChannel = "drawing_changed").
//   테이블은 직접 사용하지 않음(알림은 데이터 없이 "변경 있음" 신호만 전달하고,
//   실제 변경 조회는 ChangeFeedRepository.GetChangesSinceAsync가 change_log에서 수행).
// 트랜잭션·락 정책: 트랜잭션 없음. 풀링을 쓰지 않는 전용 연결 1개를 장기 점유한다.
//   이벤트(ChangesAvailable, ConnectionStateChanged)는 백그라운드/타이머 스레드에서 발생할 수 있으므로
//   구독자는 UI 스레드로 마샬링해야 한다.
// ============================================================================
using Npgsql;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Listens for <c>drawing_changed</c> notifications on a dedicated connection. It reconnects with a
/// growing delay after a failure, and raises <see cref="ChangesAvailable"/> (1) on every
/// notification, (2) right after each (re)connect and (3) once per safety-net interval, so a
/// notification lost while disconnected — or never delivered — is still picked up.
/// The event may fire on a background thread.
/// </summary>
/// <remarks>
/// (한국어 요약) 전용 연결에서 <c>drawing_changed</c> 알림을 수신한다. 실패하면 점점 늘어나는 지연으로 재연결하며,
/// 알림 수신 시 / (재)연결 직후 / 안전망 주기마다 <see cref="ChangesAvailable"/>을 발생시켜
/// 연결이 끊긴 동안 놓쳤거나 전달되지 않은 알림도 결국 처리되게 한다. 이벤트는 백그라운드 스레드에서 발생할 수 있다.
/// </remarks>
public sealed class ChangeListener : IAsyncDisposable
{
    /// <summary>재연결 시도 간 대기 시간표(1,2,5,10,30초). 시도 횟수가 넘치면 마지막 값(30초)을 계속 사용한다.</summary>
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
    };

    /// <summary>KeepAlive/Pooling이 적용된 알림 전용 연결 문자열.</summary>
    private readonly string connectionString;
    /// <summary>알림 유실 대비 안전망 타이머 주기.</summary>
    private readonly TimeSpan safetyInterval;
    /// <summary>수신 루프와 재시도 대기를 중단시키는 취소 토큰 소스.</summary>
    private readonly CancellationTokenSource stopping = new();
    /// <summary>백그라운드 수신 루프 태스크. null이면 아직 Start되지 않은 상태(중복 Start 방지에도 사용).</summary>
    private Task? loop;
    /// <summary>주기적으로 ChangesAvailable을 발생시키는 안전망 타이머.</summary>
    private Timer? safetyTimer;
    /// <summary>Dispose 중복 호출 방지 플래그(0=미해제, 1=해제됨; Interlocked로 갱신).</summary>
    private int disposed;

    /// <summary>
    /// 서버 변경 가능성을 알리는 이벤트(알림 수신 / (재)연결 직후 / 안전망 주기). 백그라운드 스레드에서 발생할 수 있다.
    /// 인자가 없으므로 구독자는 직접 변경 내역을 조회해야 한다.
    /// </summary>
    public event Action? ChangesAvailable;

    /// <summary>True when the notification connection is up, false while reconnecting.</summary>
    /// <remarks>알림 연결 상태가 바뀔 때만 발생한다(true=연결됨, false=끊김/재연결 중).</remarks>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>현재 알림 연결이 살아 있는지 여부.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>리스너를 만든다(아직 연결하지 않음; <see cref="Start"/>를 호출해야 시작).</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열. KeepAlive=30초, Pooling=false가 덮어써진다.</param>
    /// <param name="safetyInterval">안전망 이벤트 주기. null이면 60초.</param>
    public ChangeListener(string connectionString, TimeSpan? safetyInterval = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Detects a silently dropped connection (e.g. server restart) while waiting for notifications.
            // (서버 재시작 등으로 조용히 끊긴 연결을 알림 대기 중에도 감지)
            KeepAlive = 30,
            // 풀링 비활성화: LISTEN을 거는 장기 전용 연결이므로 연결 풀 대상에서 제외한다
            Pooling = false
        };
        this.connectionString = builder.ConnectionString;
        this.safetyInterval = safetyInterval ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// 안전망 타이머와 수신 루프를 시작한다. 이미 시작된 경우 아무 일도 하지 않는다(멱등).
    /// </summary>
    public void Start()
    {
        if (loop is not null)
        {
            return;
        }

        // 1) 알림이 유실되어도 주기마다 한 번은 변경 조회를 유도
        safetyTimer = new Timer(_ => ChangesAvailable?.Invoke(), null, safetyInterval, safetyInterval);
        // 2) 연결/LISTEN/재연결 루프를 백그라운드로 실행
        loop = Task.Run(() => RunAsync(stopping.Token));
    }

    /// <summary>연결 상태를 갱신하고, 값이 바뀐 경우에만 <see cref="ConnectionStateChanged"/>를 발생시킨다.</summary>
    /// <param name="connected">새 연결 상태.</param>
    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        ConnectionStateChanged?.Invoke(connected);
    }

    /// <summary>
    /// 수신 루프 본체: 연결 → LISTEN → 알림 대기를 반복하고, 실패 시 지연 후 재연결한다.
    /// 취소되면 정상 종료하며 마지막에 연결 상태를 false로 알린다.
    /// </summary>
    /// <param name="cancellationToken">루프 종료 요청 토큰(Dispose 시 취소됨).</param>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // 연속 실패 횟수: RetryDelays 인덱스로 사용, 연결 성공 시 0으로 초기화
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1) 전용 연결 열기
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                // 2) 알림이 오면 데이터 없이 "변경 있음" 신호만 올림
                connection.Notification += (_, _) => ChangesAvailable?.Invoke();
                // 3) 채널 구독(LISTEN). 채널명은 상수이므로 문자열 보간이 안전하다
                await using (var listen = new NpgsqlCommand($"LISTEN {ChangeFeedRepository.NotifyChannel};", connection))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                }

                // 4) 성공: 재시도 카운터 초기화 및 상태 알림
                attempt = 0;
                SetConnected(true);
                ChangesAvailable?.Invoke(); // catch up on anything missed while not listening

                // 5) 알림이 올 때까지 대기(수신 시 Notification 핸들러가 호출됨). 연결이 끊기면 예외 발생
                while (!cancellationToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 종료 요청에 의한 취소: 루프 탈출
                break;
            }
            catch
            {
                // 연결/수신 실패: 상태를 끊김으로 알리고 점증하는 지연 후 재시도
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

    /// <summary>
    /// 리스너를 중지하고 자원을 해제한다. 여러 번 호출해도 한 번만 수행된다.
    /// 루프 종료는 최대 3초까지만 기다리며 그 이후/실패는 무시한다.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 이미 해제되었으면 즉시 반환(스레드 안전한 1회 실행 보장)
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        // 1) 루프와 재시도 대기에 취소 신호
        stopping.Cancel();
        // 2) 안전망 타이머 해제
        if (safetyTimer is not null)
        {
            await safetyTimer.DisposeAsync();
        }

        // 3) 루프가 끝나길 최대 3초 대기
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Shutting down: nothing useful to do with a late failure.
                // (종료 중: 늦게 발생한 실패는 처리할 방법이 없으므로 무시)
            }
        }

        stopping.Dispose();
    }
}
