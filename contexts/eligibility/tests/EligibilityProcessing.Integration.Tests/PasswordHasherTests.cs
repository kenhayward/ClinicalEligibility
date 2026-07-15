using EligibilityProcessing.Web.Auth;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class PasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_wrong_password_fails()
    {
        var hash = _hasher.Hash("right");
        Assert.False(_hasher.Verify("wrong", hash));
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_differ()
    {
        var a = _hasher.Hash("same");
        var b = _hasher.Hash("same");
        Assert.NotEqual(a, b);
        Assert.True(_hasher.Verify("same", a));
        Assert.True(_hasher.Verify("same", b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-bcrypt-hash")]
    public void Verify_returns_false_for_empty_or_malformed_hash(string hash)
    {
        Assert.False(_hasher.Verify("anything", hash));
    }
}
