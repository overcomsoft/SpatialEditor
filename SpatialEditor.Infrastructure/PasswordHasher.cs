// ============================================================================
// PasswordHasher 전체 흐름도
//
//   등록/변경:  ValidatePolicy(정책 검사) → Hash(무작위 salt 생성 → PBKDF2-SHA256 → 문자열 조립)
//               → 호출자가 "PBKDF2-SHA256$반복횟수$salt$hash" 문자열을 DB(사용자 테이블)에 저장
//   로그인:     DB에서 읽은 저장 문자열 → Verify(분해/검증 → 저장된 반복횟수·salt로 재계산
//               → 고정 시간 비교) → true/false
//
// 외부 의존: System.Security.Cryptography 만 사용(DB 접근·테이블 없음, 상태 없음).
// 트랜잭션·락 정책: 없음(순수 함수형 정적 클래스, 스레드 안전).
// 비밀번호 평문은 저장·로그 출력하지 않는다.
// ============================================================================
using System.Security.Cryptography;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// PBKDF2-SHA256 password hashing with a random per-password salt. The stored string carries its
/// own parameters ("PBKDF2-SHA256$iterations$salt$hash", base64), so the iteration count can be
/// raised later without breaking existing accounts. Passwords are never stored or logged.
/// (한국어 요약) 비밀번호마다 무작위 salt를 쓰는 PBKDF2-SHA256 해시. 저장 문자열에 알고리즘·반복횟수·salt·hash가
/// 모두 들어 있어, 나중에 반복횟수를 올려도 기존 계정이 깨지지 않는다. 평문은 저장/로그에 남기지 않는다.
/// </summary>
public static class PasswordHasher
{
    /// <summary>비밀번호 최소 길이(문자 수). <see cref="ValidatePolicy"/>에서 사용한다.</summary>
    public const int MinimumLength = 8;
    /// <summary>저장 문자열의 첫 토큰으로 쓰이는 알고리즘 식별자.</summary>
    private const string Algorithm = "PBKDF2-SHA256";
    /// <summary>신규 해시 생성 시 PBKDF2 반복 횟수. 검증 시에는 저장된 값을 사용하므로 올려도 기존 계정은 유효하다.</summary>
    private const int Iterations = 210_000;
    /// <summary>salt 길이(바이트).</summary>
    private const int SaltSize = 16;
    /// <summary>파생 키(해시) 길이(바이트).</summary>
    private const int KeySize = 32;

    /// <summary>
    /// 비밀번호를 무작위 salt와 함께 PBKDF2-SHA256으로 해시하여 저장용 문자열을 만든다.
    /// </summary>
    /// <param name="password">평문 비밀번호(null 불가). 정책 검사는 <see cref="ValidatePolicy"/>가 별도로 수행한다.</param>
    /// <returns>"PBKDF2-SHA256$반복횟수$salt(base64)$hash(base64)" 형식의 문자열. 호출할 때마다 salt가 달라 결과도 달라진다.</returns>
    public static string Hash(string password)
    {
        // 1) 암호학적으로 안전한 난수로 salt 생성(같은 비밀번호라도 해시가 달라지게 함)
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        // 2) PBKDF2-SHA256으로 키 파생(반복 횟수가 많을수록 무차별 대입이 느려짐)
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        // 3) 검증에 필요한 모든 파라미터를 문자열 하나에 담아 반환
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// 입력한 비밀번호가 저장된 해시 문자열과 일치하는지 검증한다.
    /// </summary>
    /// <param name="password">사용자가 입력한 평문 비밀번호.</param>
    /// <param name="stored"><see cref="Hash"/>가 만든 저장 문자열. 형식이 다르면 false를 반환한다(예외 없음).</param>
    /// <returns>일치하면 true. 형식 오류·알고리즘 불일치·base64 오류·값 불일치는 모두 false.</returns>
    public static bool Verify(string password, string stored)
    {
        // 1) 저장 문자열을 '$'로 분해하여 4개 토큰·알고리즘·반복횟수(1 이상)를 확인
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            // 2) 저장된 salt/기대 해시를 복원하고, 저장된 반복횟수·같은 길이로 다시 파생
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            // 3) 타이밍 공격을 막기 위해 고정 시간 비교를 사용
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            // base64가 깨진 저장 값은 불일치로 처리
            return false;
        }
    }

    /// <summary>Returns a message when the password is not acceptable, otherwise null.</summary>
    /// <param name="password">검사할 비밀번호. null/빈 문자열도 허용되며 부적합으로 판정된다.</param>
    /// <returns>부적합하면 사용자에게 보여줄 안내 메시지(영문), 적합하면 null.</returns>
    public static string? ValidatePolicy(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinimumLength
            ? $"Password must be at least {MinimumLength} characters."
            : null;
}
