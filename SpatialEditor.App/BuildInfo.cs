using System.Globalization;
using System.Reflection;

// ============================================================================
// BuildInfo : 개발 빌드 번호(빌드한 날짜·시각) 제공
// ----------------------------------------------------------------------------
// 흐름도
//   빌드 시 어셈블리 메타데이터 "BuildStamp"(yyyyMMddHHmmss) 기록
//     → 첫 접근 시 ReadStamp() 가 리플렉션으로 값을 읽음(없으면 "unknown")
//     → Number(원본 14자리) / Display(yyyy-MM-dd HH:mm:ss, 파싱 실패 시 원본) / Label("Build ...")
//     → 창 제목과 정보(About) 창에서 표시
// ============================================================================
namespace SpatialEditor.App;

/// <summary>개발 빌드 번호: 프로그램을 빌드한 날짜와 시각(yyyyMMddHHmmss).</summary>
internal static class BuildInfo
{
    /// <summary>14자리 원본 스탬프(예: 20261008153045). 메타데이터가 없으면 "unknown".</summary>
    public static string Number { get; } = ReadStamp();

    /// <summary>읽기 쉬운 형태(예: 2026-10-08 15:30:45). 파싱에 실패하면 원본 문자열.</summary>
    public static string Display { get; } =
        DateTime.TryParseExact(Number, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var built)
            ? built.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : Number;

    /// <summary>창 제목과 정보 창에 쓰는 문구(예: "Build 20261008153045").</summary>
    public static string Label => $"Build {Number}";

    /// <summary>어셈블리의 AssemblyMetadata("BuildStamp") 값을 읽습니다.</summary>
    /// <returns>스탬프 문자열. 없으면 "unknown".</returns>
    private static string ReadStamp() =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildStamp")?.Value ?? "unknown";
}
