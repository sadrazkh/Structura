using System.Security.Cryptography;
using System.Text;
using System.Web;
using FluentAssertions;
using Structura.Web.Infrastructure.Telegram;
using Xunit;

namespace Structura.Tests.Unit;

public class MiniAppAuthTests
{
    private const string BotToken = "123456:TEST-BOT-TOKEN-abcdef";

    /// <summary>Builds a correctly-signed initData string the way Telegram would.</summary>
    private static string BuildInitData(string userJson, long authDateUnix)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDateUnix.ToString(),
            ["query_id"] = "AAABBBCCC",
            ["user"] = userJson,
        };
        var dataCheckString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(BotToken));
        var hash = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString))).ToLowerInvariant();

        var query = HttpUtility.ParseQueryString("");
        foreach (var (k, v) in fields) query[k] = v;
        query["hash"] = hash;
        return query.ToString()!;
    }

    private static readonly string UserJson =
        """{"id":555123,"first_name":"Sara","last_name":"Ahmadi","username":"sara_a"}""";

    [Fact]
    public void Valid_init_data_is_accepted_and_user_parsed()
    {
        var initData = BuildInitData(UserJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        MiniAppAuth.TryValidate(initData, BotToken, TimeSpan.FromHours(24), out var user).Should().BeTrue();
        user!.TelegramUserId.Should().Be(555123);
        user.Username.Should().Be("sara_a");
        user.FullName.Should().Be("Sara Ahmadi");
    }

    [Fact]
    public void Tampered_hash_is_rejected()
    {
        var initData = BuildInitData(UserJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var tampered = initData.Replace("Sara", "Attacker");
        MiniAppAuth.TryValidate(tampered, BotToken, TimeSpan.FromHours(24), out _).Should().BeFalse();
    }

    [Fact]
    public void Wrong_bot_token_is_rejected()
    {
        var initData = BuildInitData(UserJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        MiniAppAuth.TryValidate(initData, "999999:DIFFERENT-TOKEN", TimeSpan.FromHours(24), out _).Should().BeFalse();
    }

    [Fact]
    public void Stale_auth_date_is_rejected()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeSeconds();
        var initData = BuildInitData(UserJson, old);
        MiniAppAuth.TryValidate(initData, BotToken, TimeSpan.FromHours(24), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage-without-hash")]
    [InlineData("hash=notvalidhex&auth_date=123")]
    public void Malformed_init_data_is_rejected(string initData) =>
        MiniAppAuth.TryValidate(initData, BotToken, TimeSpan.FromHours(24), out _).Should().BeFalse();

    [Fact]
    public void Markdown_escaping_neutralizes_formatting_characters()
    {
        TelegramMarkdown.Escape("Project *bold* [x](y) _under_ .!")
            .Should().Be(@"Project \*bold\* \[x\]\(y\) \_under\_ \.\!");
    }
}
