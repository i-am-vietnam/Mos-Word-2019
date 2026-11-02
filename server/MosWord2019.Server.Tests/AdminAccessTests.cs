using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

public sealed class AdminAccessTests
{
    [Theory]
    [InlineData("owner\n")]
    [InlineData("owner ")]
    [InlineData("ab")]
    [InlineData("người-dùng")]
    public void UsernameMustMatchEntireAsciiIdentifier(string username) =>
        Assert.Throws<ArgumentException>(() => AdminAccess.NormalizeUsername(username));

    [Fact]
    public void ExactExpiryBoundaryRevokesAdminAccess()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(AdminAccess.IsAllowed(new Account { IsAdmin = true, ExpiresAt = now }, now));
        Assert.True(AdminAccess.IsAllowed(new Account { IsAdmin = true, ExpiresAt = now.AddTicks(1) }, now));
    }
}
