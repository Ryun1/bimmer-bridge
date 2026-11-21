using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class TokenAuthTests
{
    // 43 chars, base64url, like the real token.
    private const string Token = "q3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxEw";

    [Fact]
    public void CorrectToken_Authorized()
    {
        var auth = new TokenAuth(Token);
        Assert.True(auth.IsAuthorized("Bearer " + Token));
    }

    [Fact]
    public void LowercaseScheme_Accepted()
    {
        var auth = new TokenAuth(Token);
        Assert.True(auth.IsAuthorized("bearer " + Token));
        Assert.True(auth.IsAuthorized("BEARER " + Token));
    }

    [Fact]
    public void WrongToken_SameLength_Rejected()
    {
        var auth = new TokenAuth(Token);
        var wrong = "X" + Token[1..];
        Assert.False(auth.IsAuthorized("Bearer " + wrong));
    }

    [Theory]
    [InlineData("q3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxE")]   // one short
    [InlineData("q3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxEwA")] // one long
    [InlineData("q")]
    [InlineData("q3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxEwq3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxEw")]
    public void WrongLength_Rejected(string presented)
    {
        var auth = new TokenAuth(Token);
        Assert.False(auth.IsAuthorized("Bearer " + presented));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void EmptyConfiguredToken_NeverAuthenticates(string configured)
    {
        var auth = new TokenAuth(configured);
        Assert.False(auth.IsAuthorized("Bearer " + configured));
        Assert.False(auth.IsAuthorized("Bearer x"));
        Assert.False(auth.IsAuthorized("Bearer "));
        Assert.False(auth.IsAuthorized("Bearer"));
        Assert.False(auth.Matches(configured));
        Assert.False(auth.Matches(""));
    }

    [Fact]
    public void NullConfiguredToken_NeverAuthenticates()
    {
        var auth = new TokenAuth(null);
        Assert.False(auth.IsAuthorized("Bearer x"));
        Assert.False(auth.Matches(""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer  " + Token)]          // two spaces
    [InlineData(" Bearer " + Token)]          // leading whitespace
    [InlineData("\tBearer " + Token)]
    [InlineData("Bearer " + Token + " ")]     // trailing whitespace
    [InlineData("Bearer " + Token + " " + Token)] // two tokens
    [InlineData("Bearer\t" + Token)]
    [InlineData("Basic " + Token)]
    [InlineData("Basic x")]
    [InlineData("Token " + Token)]
    [InlineData(Token)]
    [InlineData("Bearer: " + Token)]
    [InlineData("Bearer " + Token + "\r\n")]
    public void MalformedHeaders_Rejected(string? header)
    {
        var auth = new TokenAuth(Token);
        Assert.False(auth.IsAuthorized(header));
    }

    [Theory]
    [InlineData("Bearer abc", true, "abc")]
    [InlineData("bearer abc", true, "abc")]
    [InlineData("bEaReR abc", true, "abc")]
    [InlineData("Bearer", false, null)]
    [InlineData("Bearer  x", false, null)]
    [InlineData("Basic x", false, null)]
    [InlineData("Bearer a b", false, null)]
    [InlineData(" Bearer x", false, null)]
    [InlineData("Bearer x\u0000", false, null)]
    public void TryParseBearer(string header, bool ok, string? token)
    {
        Assert.Equal(ok, TokenAuth.TryParseBearer(header, out var parsed));
        if (ok)
        {
            Assert.Equal(token, parsed);
        }
    }

    [Fact]
    public void Matches_ComparesExactly()
    {
        var auth = new TokenAuth(Token);
        Assert.True(auth.Matches(Token));
        Assert.False(auth.Matches(Token.ToUpperInvariant()));
        Assert.False(auth.Matches(""));
        Assert.False(auth.Matches(null));
    }

    [Fact]
    public void FileContent_IsTrimmed()
    {
        var r = TokenAuth.ParseTokenFile("\uFEFF  " + Token + "\r\n");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Token, r.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("Xq7zK")]
    [InlineData("0123456789012345678901234567890")] // 31 chars
    public void FileContent_TooShort_Rejected(string content)
    {
        var r = TokenAuth.ParseTokenFile(content);
        Assert.False(r.Ok);
        Assert.Null(r.Token);
        Assert.False(string.IsNullOrEmpty(r.Error));
        // The error must never echo the token material.
        if (content.Trim().Length > 0)
        {
            Assert.DoesNotContain(content.Trim(), r.Error!);
        }
    }

    [Fact]
    public void FileContent_Exactly32_Accepted()
    {
        var r = TokenAuth.ParseTokenFile("01234567890123456789012345678901");
        Assert.True(r.Ok);
    }

    [Fact]
    public void FileContent_InnerWhitespace_Rejected()
    {
        var r = TokenAuth.ParseTokenFile("0123456789012345 6789012345678901234");
        Assert.False(r.Ok);
    }

    [Fact]
    public void Null_FileContent_Rejected()
    {
        Assert.False(TokenAuth.ParseTokenFile(null).Ok);
    }
}
