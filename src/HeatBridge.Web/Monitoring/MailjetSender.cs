using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeatBridge.Web.Monitoring;

public sealed record MailResult(bool Accepted, bool Retryable, string? Error = null, TimeSpan? RetryAfter = null);

public sealed class MailjetSender(IHttpClientFactory clients, IConfiguration configuration)
{
    public string Fingerprint(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var key = Secret("ApiKey", "mailjet-api-key");
        var secret = Secret("SecretKey", "mailjet-secret-key");
        var sender = configuration["HeatBridge:Mailjet:FromEmail"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret) || !MailAddress.TryCreate(sender, out _)) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}\n{secret}\n{sender}\n{string.Join(';', settings.Addresses())}")));
    }

    public bool IsReady(NotificationSettings settings) => settings.TestedConfiguration is { Length: > 0 } tested && tested == Fingerprint(settings);

    public async Task<MailResult> SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        var sender = configuration["HeatBridge:Mailjet:FromEmail"];
        var key = Secret("ApiKey", "mailjet-api-key");
        var secret = Secret("SecretKey", "mailjet-secret-key");
        if (!MailAddress.TryCreate(recipient, out _) || !MailAddress.TryCreate(sender, out _) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            return new(false, false, "Mailjet saknar giltig avsändare, mottagare eller API-nycklar.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mailjet.com/v3.1/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")));
        request.Content = JsonContent.Create(new
        {
            Messages = new[] { new { From = new { Email = sender, Name = "HeatBridge" }, To = new[] { new { Email = recipient } }, Subject = subject, TextPart = body } },
        }, options: JsonSerializerOptions.Default);
        try
        {
            using var response = await clients.CreateClient("Mailjet").SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var retry = response.Headers.RetryAfter?.Delta;
                if (retry is null && response.Headers.RetryAfter?.Date is { } date) retry = date - DateTimeOffset.UtcNow;
                if (retry is { } delay) retry = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 86400));
                return new(false, status == 429 || status >= 500, $"Mailjet svarade med HTTP {status}.", retry);
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var accepted = json.RootElement.TryGetProperty("Messages", out var messages) && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() == 1 && messages[0].ValueKind == JsonValueKind.Object
                && messages[0].TryGetProperty("Status", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() == "success";
            return accepted ? new(true, false) : new(false, false, "Mailjet accepterade inte meddelandet. Kontrollera avsändaren i Mailjet.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, true, "Mailjet svarade inte i tid."); }
        catch (HttpRequestException) { return new(false, true, "Kunde inte ansluta till Mailjet."); }
        catch (JsonException) { return new(false, true, "Mailjet gav ett oväntat svar."); }
    }

    private string? Secret(string setting, string credential)
    {
        var directory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var path = Path.Combine(directory, credential);
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        return configuration[$"HeatBridge:Mailjet:{setting}"];
    }
}
