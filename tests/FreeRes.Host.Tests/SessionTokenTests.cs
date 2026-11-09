namespace FreeRes.Host.Tests;

public sealed class SessionTokenTests
{
    [Fact]
    public void Jeton_de_256_bits_en_base64url_et_unique()
    {
        var a = SessionToken.Create();
        var b = SessionToken.Create();
        Assert.Equal(43, a.Length);                     // 32 octets -> 43 caracteres base64url sans remplissage
        Assert.Matches("^[A-Za-z0-9_-]+$", a);          // utilisable tel quel dans un cookie
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("abcd", true)]
    [InlineData("abcde", false)]
    public void Comparaison_exacte(string? candidate, bool expected) =>
        Assert.Equal(expected, SessionToken.Matches(candidate, "abcd"));
}
