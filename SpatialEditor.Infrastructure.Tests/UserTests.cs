using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.Infrastructure.Tests;

public class UserTests
{
    [Fact]
    public void HashedPasswordVerifiesAndWrongPasswordFails()
    {
        var hash = PasswordHasher.Hash("correct horse battery");

        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("wrong password", hash));
        Assert.False(PasswordHasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void SamePasswordGetsADifferentSaltAndNeverStoresThePlainText()
    {
        var first = PasswordHasher.Hash("same-password");
        var second = PasswordHasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("same-password", first);
        Assert.StartsWith("PBKDF2-SHA256$", first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2-SHA256$abc$def$ghi")]
    [InlineData("OTHER$1000$AAAA$AAAA")]
    public void MalformedStoredHashesAreRejectedWithoutThrowing(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void ShortPasswordsViolateThePolicy(string? password)
    {
        Assert.NotNull(PasswordHasher.ValidatePolicy(password));
    }

    [Fact]
    public void MinimumLengthPasswordSatisfiesThePolicy()
    {
        Assert.Null(PasswordHasher.ValidatePolicy(new string('a', PasswordHasher.MinimumLength)));
    }

    [Fact]
    public void DefaultRolesOnlyGrantKnownPermissionsAndAdminGetsAll()
    {
        var known = Permissions.All.Select(item => item.Key).ToHashSet();

        Assert.Equal(known.Count, Permissions.All.Count);
        foreach (var (_, _, keys) in Permissions.DefaultRoles)
        {
            Assert.All(keys, key => Assert.Contains(key, known));
        }

        var admin = Permissions.DefaultRoles.Single(role => role.Name == "Admin");
        Assert.Equal(known.OrderBy(k => k), admin.Keys.OrderBy(k => k));
    }

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

    [Fact]
    public void AppUserReportsGrantedPermissions()
    {
        var user = new AppUser(1, "kim", "Kim", true, false, null, null, new[] { "Viewer" },
            new HashSet<string> { Permissions.DrawingView });

        Assert.True(user.Has(Permissions.DrawingView));
        Assert.False(user.Has(Permissions.DrawingEdit));
    }
}
