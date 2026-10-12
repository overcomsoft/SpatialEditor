// ============================================================================
// ImportAuditLogger 전체 흐름도
//
//   Write(message)
//     → LogDirectory(%LOCALAPPDATA%\SpatialEditor\logs) 폴더 보장(CreateDirectory)
//     → 오늘 날짜 파일명 결정(import-yyyyMMdd.log)
//     → "시각(밀리초 포함) + 메시지" 한 줄을 파일 끝에 추가(AppendAllText)
//
// 사용하는 외부 의존: 로컬 파일 시스템만 사용(DB 테이블 없음).
// 트랜잭션·락 정책: 없음. 파일 단위 잠금/동기화를 하지 않으므로 단일 호출자(UI 스레드의
// 가져오기 작업) 기준의 단순 감사 로그이다. 날짜가 바뀌면 자동으로 새 파일에 기록된다.
// ============================================================================
namespace SpatialEditor.Infrastructure;

/// <summary>
/// DXF 가져오기(import) 과정의 감사 로그를 일자별 텍스트 파일로 남기는 단순 로거.
/// 로그는 사용자 로컬 앱 데이터 폴더에 저장되며 DB에는 기록하지 않는다.
/// </summary>
public sealed class ImportAuditLogger
{
    /// <summary>
    /// 로그 파일이 저장되는 폴더 경로(%LOCALAPPDATA%\SpatialEditor\logs). 폴더가 없어도 경로만 반환한다.
    /// </summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "logs");

    /// <summary>
    /// 로그 한 줄을 당일 파일(import-yyyyMMdd.log) 끝에 추가한다.
    /// 각 줄은 "yyyy-MM-dd HH:mm:ss.fff 메시지" 형식이다(로컬 시간 기준).
    /// </summary>
    /// <param name="message">기록할 메시지. 줄바꿈은 호출자가 정리해서 넘기는 것을 전제로 하며 null은 허용하지 않는다.</param>
    /// <exception cref="IOException">파일 접근(권한/잠금) 문제가 있을 때 발생할 수 있으며 여기서 처리하지 않는다.</exception>
    public void Write(string message)
    {
        // 1) 폴더가 없으면 생성(이미 있으면 아무 일도 하지 않음)
        Directory.CreateDirectory(LogDirectory);
        // 2) 날짜별로 파일을 분리하여 로그가 한 파일에 무한정 커지지 않게 한다
        var path = Path.Combine(LogDirectory, $"import-{DateTime.Now:yyyyMMdd}.log");
        // 3) 밀리초까지 포함한 시각을 앞에 붙여 한 줄 추가
        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
}