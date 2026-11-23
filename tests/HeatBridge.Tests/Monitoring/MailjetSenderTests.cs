using System.Net;
using System.Text.Json;
using HeatBridge.Tests.TestSupport;
using HeatBridge.Web.Monitoring;
using Microsoft.Extensions.Configuration;

namespace HeatBridge.Tests.Monitoring;

public class MailjetSenderTests
{
    private static IConfiguration Configuration(string secret = "test-secret") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["HeatBridge:Mailjet:ApiKey"] = "test-key", ["HeatBridge:Mailjet:SecretKey"] = secret,
        ["HeatBridge:Mailjet:FromEmail"] = "heatbridge@example.test",
    }).Build();

    [Fact]
    public async Task Send_UsesFixedHttpsEndpointAndSeparateRecipient()
    {
        var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            Assert.Equal("https://api.mailjet.com/v3.1/send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("person@example.test", body.RootElement.GetProperty("Messages")[0].GetProperty("To")[0].GetProperty("Email").GetString());
            return StubHttpMessageHandler.JsonResponse("""{"Messages":[{"Status":"success"}]}""");
        });
        var mail = new MailjetSender(new StubHttpClientFactory(handler), Configuration());
        Assert.True((await mail.SendAsync("person@example.test", "Test", "Test body", CancellationToken.None)).Accepted);
    }

    [Theory]
    [InlineData(401, false)] [InlineData(400, false)] [InlineData(429, true)] [InlineData(503, true)]
    public async Task HttpFailures_AreClassifiedForRetry(int status, bool retry)
    {
        var mail = new MailjetSender(new StubHttpClientFactory(StubHttpMessageHandler.Json("{}", (HttpStatusCode)status)), Configuration());
        var result = await mail.SendAsync("person@example.test", "Test", "Body", CancellationToken.None);
        Assert.False(result.Accepted); Assert.Equal(retry, result.Retryable);
    }

    [Fact]
    public async Task RateLimit_RespectsRetryAfter()
    {
        var handler = StubHttpMessageHandler.Route(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(3)); return response;
        });
        var mail = new MailjetSender(new StubHttpClientFactory(handler), Configuration());
        Assert.Equal(TimeSpan.FromMinutes(3), (await mail.SendAsync("person@example.test", "Test", "Body", CancellationToken.None)).RetryAfter);
    }

    [Theory]
    [InlineData("{\"Messages\":[{\"Status\":\"error\"}]}")]
    [InlineData("{\"Messages\":[]}")]
    [InlineData("not-json")]
    public async Task HttpSuccessWithoutMessageAcceptance_IsNotSuccess(string response)
    {
        var mail = new MailjetSender(new StubHttpClientFactory(StubHttpMessageHandler.Json(response)), Configuration());
        Assert.False((await mail.SendAsync("person@example.test", "Test", "Body", CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task TimeoutAndNetworkFailure_AreRetryable()
    {
        foreach (var exception in new Exception[] { new HttpRequestException(), new TaskCanceledException() })
        {
            var mail = new MailjetSender(new StubHttpClientFactory(StubHttpMessageHandler.Throws(exception)), Configuration());
            Assert.True((await mail.SendAsync("person@example.test", "Test", "Body", CancellationToken.None)).Retryable);
        }
    }

    [Fact]
    public void ChangedCredentialsOrRecipients_RequireNewTest()
    {
        var factory = new StubHttpClientFactory(StubHttpMessageHandler.Json("{}"));
        var mail = new MailjetSender(factory, Configuration());
        var settings = new NotificationSettings { Recipients = "person@example.test" };
        settings.TestedConfiguration = mail.Fingerprint(settings);
        Assert.True(mail.IsReady(settings));
        Assert.False(new MailjetSender(factory, Configuration("changed")).IsReady(settings));
        settings.Recipients = "other@example.test"; Assert.False(mail.IsReady(settings));
    }
}
