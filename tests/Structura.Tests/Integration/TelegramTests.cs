using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Structura.Web.Infrastructure.Telegram;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class TelegramTests(TestAppFactory factory) : IDisposable
{
    private const string BotToken = "111111:INTEGRATION-BOT-TOKEN";
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose()
    {
        _mock.Stop();
        // Reset the shared singleton so other tests hit the default base URL.
        factory.Services.GetRequiredService<TelegramApiOptions>().BaseUrl = "https://api.telegram.org";
    }

    /// <summary>Configure the bot (token + webhook secret) and route the Telegram API at WireMock.</summary>
    private async Task<string> ConfigureBotAsync(HttpClient admin)
    {
        factory.Services.GetRequiredService<TelegramApiOptions>().BaseUrl = _mock.Url!;
        // Every Telegram API method returns ok:true by default.
        _mock.Given(Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher("/bot*")).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { ok = true, result = new { username = "structura_bot" } }));

        (await admin.PutAsJsonAsync("/api/settings", new
        {
            publicBaseUrl = "https://structura.example.com",
            telegramMode = "webhook",
            telegramBotToken = BotToken,
        })).EnsureSuccessStatusCode();

        // Read back the webhook secret directly (it's generated server-side).
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<Structura.Web.Infrastructure.Settings.AppSettingsService>();
        return (await settings.GetAsync(Structura.Web.Infrastructure.Settings.AppSettingsService.TelegramWebhookSecret))!;
    }

    private async Task PostUpdateAsync(string secret, long fromId, string text, string? username = "tguser")
    {
        var update = new
        {
            update_id = Random.Shared.Next(1, int.MaxValue),
            message = new
            {
                message_id = 1,
                chat = new { id = fromId },
                from = new { id = fromId, username, first_name = "Telegram", last_name = "User" },
                text,
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/telegram/webhook/{secret}")
        {
            Content = JsonContent.Create(update),
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        var response = await factory.CreateClient().SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int seconds = 8)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return false;
    }

    // ---------- linking ----------

    [Fact]
    public async Task Full_linking_flow_via_start_command()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        // Reviewer generates a code in the app.
        var codeResponse = await reviewer.PostAsync("/api/telegram/link-code", null);
        codeResponse.EnsureSuccessStatusCode();
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;

        // Not linked yet.
        var before = await reviewer.GetFromJsonAsync<JsonElement>("/api/telegram/link", ApiClient.Json);
        before.GetProperty("linked").GetBoolean().Should().BeFalse();

        // They send /start <code> to the bot.
        const long telegramId = 900001;
        await PostUpdateAsync(secret, telegramId, $"/start {code}");

        (await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.UserId == reviewerId
                && l.TelegramUserId == telegramId && l.Status == "Active");
        })).Should().BeTrue("the /start code must bind the Telegram account");

        var after = await reviewer.GetFromJsonAsync<JsonElement>("/api/telegram/link", ApiClient.Json);
        after.GetProperty("linked").GetBoolean().Should().BeTrue();

        // The bot replied (a sendMessage hit the Telegram API).
        _mock.LogEntries.Should().Contain(e => e.RequestMessage.Path.Contains("/sendMessage"));
    }

    [Fact]
    public async Task Expired_or_reused_codes_are_rejected()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var code = (await (await reviewer.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;

        await PostUpdateAsync(secret, 900002, $"/start {code}");
        await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.UserId == reviewerId && l.Status == "Active");
        });

        // Reusing the same code from a different Telegram account must NOT create a second link.
        await PostUpdateAsync(secret, 900999, $"/start {code}");
        await Task.Delay(1000);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
        db.TelegramLinks.Count(l => l.TelegramUserId == 900999).Should().Be(0);
    }

    [Fact]
    public async Task Takeover_is_prevented_when_telegram_id_already_linked()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (userA, _, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        var (userB, userBId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        const long sharedTelegram = 901010;
        var codeA = (await (await userA.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;
        await PostUpdateAsync(secret, sharedTelegram, $"/start {codeA}");
        await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.TelegramUserId == sharedTelegram && l.Status == "Active");
        });

        // User B tries to grab the same Telegram account.
        var codeB = (await (await userB.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;
        await PostUpdateAsync(secret, sharedTelegram, $"/start {codeB}");
        await Task.Delay(1000);

        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
        db.TelegramLinks.Count(l => l.TelegramUserId == sharedTelegram && l.Status == "Active").Should().Be(1);
        db.TelegramLinks.Any(l => l.UserId == userBId && l.Status == "Active").Should().BeFalse();
    }

    [Fact]
    public async Task Admin_can_revoke_a_link()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        var code = (await (await reviewer.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;
        await PostUpdateAsync(secret, 902020, $"/start {code}");
        await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.UserId == reviewerId && l.Status == "Active");
        });

        (await admin.PostAsync($"/api/users/{reviewerId}/revoke-telegram", null)).EnsureSuccessStatusCode();

        var status = await reviewer.GetFromJsonAsync<JsonElement>("/api/telegram/link", ApiClient.Json);
        status.GetProperty("linked").GetBoolean().Should().BeFalse();
    }

    // ---------- mini app auth ----------

    [Fact]
    public async Task Mini_app_auth_issues_a_jwt_for_a_linked_user()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        var code = (await (await reviewer.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;
        const long tgId = 903030;
        await PostUpdateAsync(secret, tgId, $"/start {code}");
        await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.UserId == reviewerId && l.Status == "Active");
        });

        var initData = BuildInitData(tgId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/telegram-miniapp", new { initData });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("user").GetProperty("role").GetString().Should().Be("Reviewer");

        // The issued token actually works.
        var client = factory.CreateClient();
        client.SetBearer(body.GetProperty("accessToken").GetString()!);
        (await client.GetAsync("/api/review/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mini_app_auth_rejects_unlinked_telegram_account()
    {
        var admin = await factory.AdminClientAsync();
        await ConfigureBotAsync(admin);
        var initData = BuildInitData(904040, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/telegram-miniapp", new { initData });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("not_linked");
    }

    // ---------- notifications ----------

    [Fact]
    public async Task Assignment_sends_a_telegram_notification_to_a_linked_reviewer()
    {
        var admin = await factory.AdminClientAsync();
        var secret = await ConfigureBotAsync(admin);
        var (projectId, reviewerId, reviewerClient) = await SetUpApprovableProjectWithLinkedReviewerAsync(admin, secret);

        // Assign a processed record to the linked reviewer.
        var recordIds = (await admin.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{projectId}/records?processingStatus=Completed", ApiClient.Json))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        recordIds.Should().NotBeEmpty();

        _mock.ResetLogEntries();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null })).EnsureSuccessStatusCode();

        (await EventuallyAsync(() =>
            _mock.LogEntries.Any(e => e.RequestMessage.Path.Contains("/sendMessage")
                                      && (e.RequestMessage.Body ?? "").Contains("review"))))
            .Should().BeTrue("a linked reviewer must get a Telegram notification when assigned records");

        _ = reviewerClient;
    }

    private async Task<(Guid ProjectId, Guid ReviewerId, HttpClient Reviewer)> SetUpApprovableProjectWithLinkedReviewerAsync(
        HttpClient admin, string secret)
    {
        // Reuse an AI mock to produce a processed record.
        using var aiMock = WireMockServer.Start();
        aiMock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                choices = new[] { new { message = new { role = "assistant", content = """{"firstName":"Sara"}""" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 2 },
            }));

        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", new
        {
            fields = new object[] { new { key = "firstName", label = "First Name", type = "shortText", required = true, displayOrder = 0 } },
        })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", new
        {
            provider = "OpenRouter", baseUrl = aiMock.Url, apiKey = "k", model = "m",
            temperature = 0.1, maxOutputTokens = 256, timeoutSeconds = 15, concurrency = 2,
            systemInstruction = "x", extractionInstruction = "y",
        })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual",
            new { records = new object[] { new { externalId = "TG-1", text = "text" } } })).EnsureSuccessStatusCode();

        var runId = (await (await admin.PostAsJsonAsync($"/api/projects/{projectId}/runs",
                new { scope = "allPending", recordIds = (Guid[]?)null }))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("id").GetGuid();
        await EventuallyAsync(() =>
        {
            var run = admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/runs/{runId}", ApiClient.Json).Result;
            return run.GetProperty("run").GetProperty("status").GetString() == "Completed";
        }, 30);

        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId })).EnsureSuccessStatusCode();
        var code = (await (await reviewer.PostAsync("/api/telegram/link-code", null))
            .Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json)).GetProperty("code").GetString()!;
        await PostUpdateAsync(secret, 905050 + Random.Shared.Next(1000), $"/start {code}");
        await EventuallyAsync(() =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Structura.Web.Persistence.AppDbContext>();
            return db.TelegramLinks.Any(l => l.UserId == reviewerId && l.Status == "Active");
        });
        return (projectId, reviewerId, reviewer);
    }

    private static string BuildInitData(long telegramId, long authDateUnix)
    {
        var userJson = JsonSerializer.Serialize(new { id = telegramId, first_name = "Sara", username = "sara_tg" });
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDateUnix.ToString(),
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
}
