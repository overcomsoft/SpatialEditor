using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// SpatialEditor.Infrastructure.Tests / UserTests : 비밀번호 해시/정책과 기본 역할·권한 규칙 단위 테스트
// ----------------------------------------------------------------------------
// [실행 조건] 데이터베이스/환경 변수 불필요(PasswordHasher, Permissions, AppUser 는 순수 메모리 로직).
// [테스트 흐름]
//   1) HashedPasswordVerifiesAndWrongPasswordFails
//        Hash 한 값이 올바른 비밀번호는 통과, 틀린 비밀번호/빈 문자열은 실패하는지
//   2) SamePasswordGetsADifferentSaltAndNeverStoresThePlainText
//        같은 비밀번호도 매번 다른 해시(솔트가 다름)이며 평문이 들어 있지 않고 "PBKDF2-SHA256$" 로 시작하는지
//   3) MalformedStoredHashesAreRejectedWithoutThrowing  [Theory]
//        형식이 깨진 저장 해시(빈 값, 임의 문자열, 필드가 잘못된 PBKDF2, 다른 알고리즘)는 예외 없이 false 인지
//   4) ShortPasswordsViolateThePolicy  [Theory]
//        null/빈 값/짧은 비밀번호는 ValidatePolicy 가 위반 사유(null 이 아님)를 돌려주는지
//   5) MinimumLengthPasswordSatisfiesThePolicy
//        최소 길이(MinimumLength)만큼의 비밀번호는 정책을 만족(null)하는지
//   6) DefaultRolesOnlyGrantKnownPermissionsAndAdminGetsAll
//        기본 역할들이 알려진 권한 키만 부여하고, 권한 키에 중복이 없으며, Admin 이 모든 권한을 갖는지
//   7) OnlyAdminCanManageUsersAndViewerIsReadOnly
//        사용자 관리 권한은 Admin 만, Viewer 는 편집/DXF 가져오기 권한 없이 보기 권한만 갖는지
//   8) AppUserReportsGrantedPermissions
//        AppUser.Has 가 부여된 권한만 true 를 돌려주는지
// ============================================================================
namespace SpatialEditor.Infrastructure.Tests;

/// <summary>비밀번호 해시/정책, 기본 역할과 권한, AppUser 권한 조회 규칙을 검증하는 테스트 모음(DB 불필요).</summary>
public class UserTests
{
    /// <summary>해시한 비밀번호는 올바른 값으로만 검증되고, 틀린 값이나 빈 문자열은 실패하는지 검증한다.</summary>
    [Fact]
    public void HashedPasswordVerifiesAndWrongPasswordFails()
    {
        // 해시 생성 후 맞는 비밀번호는 true, 틀린 비밀번호/빈 문자열은 false 여야 한다.
        var hash = PasswordHasher.Hash("correct horse battery");

        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("wrong password", hash));
        Assert.False(PasswordHasher.Verify(string.Empty, hash));
    }

    /// <summary>같은 비밀번호라도 솔트가 달라 해시가 매번 다르며, 평문을 저장하지 않고 PBKDF2-SHA256 형식으로 시작하는지 검증한다.</summary>
    [Fact]
    public void SamePasswordGetsADifferentSaltAndNeverStoresThePlainText()
    {
        // 같은 비밀번호를 두 번 해시해 서로 다른지, 평문 미포함인지, 형식 접두어가 맞는지 확인한다.
        var first = PasswordHasher.Hash("same-password");
        var second = PasswordHasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("same-password", first);
        Assert.StartsWith("PBKDF2-SHA256$", first);
    }

    /// <summary>형식이 깨진 저장 해시가 들어와도 예외를 던지지 않고 검증 실패(false)로 처리되는지 검증한다.</summary>
    /// <param name="stored">잘못된 형식의 저장 해시 문자열(빈 값, 임의 문자열, 필드가 부족한 PBKDF2, 다른 알고리즘).</param>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2-SHA256$abc$def$ghi")]
    [InlineData("OTHER$1000$AAAA$AAAA")]
    public void MalformedStoredHashesAreRejectedWithoutThrowing(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }

    /// <summary>최소 길이보다 짧거나 비어 있는(null 포함) 비밀번호가 정책 위반(사유 문자열 반환)으로 판정되는지 검증한다.</summary>
    /// <param name="password">검사할 비밀번호(null/빈 값/짧은 값).</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void ShortPasswordsViolateThePolicy(string? password)
    {
        Assert.NotNull(PasswordHasher.ValidatePolicy(password));
    }

    /// <summary>정확히 최소 길이인 비밀번호는 정책을 만족(위반 사유 없음, null)하는지 검증한다.</summary>
    [Fact]
    public void MinimumLengthPasswordSatisfiesThePolicy()
    {
        Assert.Null(PasswordHasher.ValidatePolicy(new string('a', PasswordHasher.MinimumLength)));
    }

    /// <summary>기본 역할이 알려진 권한 키만 부여하고 권한 키가 중복되지 않으며, Admin 역할이 모든 권한을 갖는지 검증한다.</summary>
    [Fact]
    public void DefaultRolesOnlyGrantKnownPermissionsAndAdminGetsAll()
    {
        // 1) 정의된 모든 권한 키 집합을 만들고 중복이 없는지(개수 비교) 확인한다.
        var known = Permissions.All.Select(item => item.Key).ToHashSet();

        Assert.Equal(known.Count, Permissions.All.Count);
        // 2) 모든 기본 역할의 권한 키가 정의된 권한 안에 있는지 확인한다.
        foreach (var (_, _, keys) in Permissions.DefaultRoles)
        {
            Assert.All(keys, key => Assert.Contains(key, known));
        }

        // 3) Admin 은 정의된 모든 권한을 정확히 갖는다.
        var admin = Permissions.DefaultRoles.Single(role => role.Name == "Admin");
        Assert.Equal(known.OrderBy(k => k), admin.Keys.OrderBy(k => k));
    }

    /// <summary>사용자 관리 권한은 Admin 에게만 있고, Viewer 는 보기 권한만 있고 편집/DXF 가져오기 권한은 없는지 검증한다.</summary>
    [Fact]
    public void OnlyAdminCanManageUsersAndViewerIsReadOnly()
    {
        var withUserManage = Permissions.DefaultRoles.Where(role => role.Keys.Contains(Permissions.UserManage)).Select(role => role.Name);
        Assert.Equal(new[] { "Admin" }, withUserManage);

        var viewer = Permissions.DefaultRoles.Single(role => role.Name == "Viewer");
        Assert.DoesNotContain(Permissions.DrawingEdit, viewer.Keys);
        Assert.DoesNotContain(Permissions.ImportDxf, viewer.Keys);
        Assert.Contains(Permissions.DrawingView, viewer.Keys);
    }

    /// <summary>AppUser.Has 가 부여된 권한에는 true, 부여되지 않은 권한에는 false 를 돌려주는지 검증한다.</summary>
    [Fact]
    public void AppUserReportsGrantedPermissions()
    {
        var user = new AppUser(1, "kim", "Kim", true, false, null, null, new[] { "Viewer" },
            new HashSet<string> { Permissions.DrawingView });

        Assert.True(user.Has(Permissions.DrawingView));
        Assert.False(user.Has(Permissions.DrawingEdit));
    }
}
