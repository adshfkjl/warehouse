using System.Security.Cryptography;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.UnitTests.Identity;

public sealed class PasswordHashingTests
{
    [Fact]
    public void Same_password_uses_distinct_salts_and_never_returns_plaintext()
    {
        var first = PasswordHashing.Hash("P@ssw0rd!");
        var second = PasswordHashing.Hash("P@ssw0rd!");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("P@ssw0rd!", first, StringComparison.Ordinal);
        Assert.True(PasswordHashing.Verify("P@ssw0rd!", first, out var needsRehash));
        Assert.False(needsRehash);
    }

    [Theory]
    [InlineData(99_999)]
    [InlineData(1_000_001)]
    public void Iterations_outside_the_supported_envelope_are_rejected(int iterations)
    {
        var envelope = Envelope("P@ssw0rd!", iterations);
        Assert.False(PasswordHashing.Verify("P@ssw0rd!", envelope, out _));
    }

    [Fact]
    public void Older_supported_iteration_count_requests_rehash()
    {
        var envelope = Envelope("P@ssw0rd!", 100_000);
        Assert.True(PasswordHashing.Verify("P@ssw0rd!", envelope, out var needsRehash));
        Assert.True(needsRehash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pbkdf2-sha256$v1$sha256$120000$bad$bad")]
    [InlineData("unknown$v1$sha256$120000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Malformed_envelope_is_rejected(string envelope)
    {
        Assert.False(PasswordHashing.Verify("P@ssw0rd!", envelope, out _));
    }

    private static string Envelope(string password, int iterations)
    {
        var salt = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{PasswordHashing.Scheme}$v{PasswordHashing.CurrentVersion}$sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
