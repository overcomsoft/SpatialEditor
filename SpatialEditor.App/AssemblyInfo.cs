using System.Windows;

// ============================================================================
// AssemblyInfo : WPF 테마 리소스 사전 위치 선언(어셈블리 수준 특성)
// ----------------------------------------------------------------------------
// 흐름도: 컴파일 시 어셈블리 특성으로 기록 → WPF 가 리소스를 못 찾을 때
//   (1) 테마별 사전 위치  (2) 일반(generic) 사전 위치 를 이 선언대로 조회
//   - 테마별 사전: 사용 안 함(None)
//   - 일반 사전: 소스 어셈블리(이 프로그램 자신)
// ============================================================================
[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
