// .NET SDK — zero dependencies (System.Text.Json + HttpClient), .NET 6+.
// Transactional email API for applications and AI agents.
//
//   var sf = new SendFlit.Client("re_...");
//   var res = await sf.SendAsync(new Email {
//       From = "you@yourdomain.com", To = "user@example.com",
//       Subject = "Confirm", Html = "<p>…</p>" });

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SendFlit;

public class SendFlitException : Exception
{
    public int Status { get; }
    public string? Body { get; }

    public SendFlitException(string message, int status = 0, string? body = null)
        : base(message)
    {
        Status = status;
        Body = body;
    }
}

public class Email
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("to_name")] public string? ToName { get; set; }
    [JsonPropertyName("cc")] public List<string>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<string>? Bcc { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("html")] public string? Html { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("reply_to")] public string? ReplyTo { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("scheduled_at")] public string? ScheduledAt { get; set; }
    [JsonPropertyName("template_id")] public string? TemplateId { get; set; }
    [JsonPropertyName("template_name")] public string? TemplateName { get; set; }
    [JsonPropertyName("variables")] public Dictionary<string, string>? Variables { get; set; }
    [JsonPropertyName("attachments")] public List<Attachment>? Attachments { get; set; }

    public void Attach(string filename, string content, string? contentType = null)
    {
        Attachments ??= new();
        Attachments.Add(new Attachment(filename,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            contentType ?? "text/plain"));
    }

    public void Attach(string filename, byte[] content, string contentType = "application/octet-stream")
    {
        Attachments ??= new();
        Attachments.Add(new Attachment(filename, Convert.ToBase64String(content), contentType));
    }
}

public class Attachment
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("content_b64")] public string ContentB64 { get; set; } = "";
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = "application/octet-stream";

    public Attachment() { }
    public Attachment(string f, string b64, string ct)
    {
        Filename = f; ContentB64 = b64; ContentType = ct;
    }
}

public class SendResult
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("to")] public string? To { get; set; }
}

public class Client : IDisposable
{
    public const string Version = "1.0.0";
    private const string DefaultBase = "https://api.sendflit.com";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly int _maxRetries;

    public Client(string apiKey, string? baseUrl = null, int timeoutSeconds = 15,
                  int maxRetries = 2)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentException("apiKey is required", nameof(apiKey));
        _baseUrl = (baseUrl ?? DefaultBase).TrimEnd('/');
        _maxRetries = Math.Max(0, maxRetries);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.Add("User-Agent", $"sendflit-dotnet/{Version}");
    }

    public async Task<SendResult> SendAsync(Email email, CancellationToken ct = default)
        => await RequestAsync<SendResult>("POST", "/v1/email", email, ct);

    public async Task<List<SendResult>> BatchAsync(List<Email> emails,
            CancellationToken ct = default)
    {
        if (emails is null || emails.Count == 0)
            throw new ArgumentException("batch requires at least one email");
        if (emails.Count > 100)
            throw new ArgumentOutOfRangeException(nameof(emails),
                "batch accepts at most 100 emails");
        var res = await RequestAsync<BatchResponse>("POST", "/v1/emails/batch",
            new { emails }, ct);
        return res.Results;
    }

    public async Task<JsonElement> ListEmailsAsync(int? limit = null, int? offset = null,
            string? status = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (limit != null) q.Add($"limit={limit}");
        if (offset != null) q.Add($"offset={offset}");
        if (status != null) q.Add($"status={Uri.EscapeDataString(status)}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        return await RequestAsync<JsonElement>("GET", "/v1/emails" + qs, null, ct);
    }

    public async Task<JsonElement> AddDomainAsync(string name, CancellationToken ct = default)
        => await RequestAsync<JsonElement>("POST", "/v1/domains", new { name }, ct);

    public async Task<JsonElement> ListDomainsAsync(CancellationToken ct = default)
        => await RequestAsync<JsonElement>("GET", "/v1/domains", null, ct);

    public async Task<JsonElement> VerifyDomainAsync(string id, CancellationToken ct = default)
        => await RequestAsync<JsonElement>("POST", $"/v1/domains/{id}/verify", null, ct);

    public async Task<JsonElement> AddContactAsync(string email, string? name = null,
            string audience = "default", CancellationToken ct = default)
        => await RequestAsync<JsonElement>("POST", "/v1/contacts",
            new { email, name, audience }, ct);

    public async Task<JsonElement> SuppressAsync(string email, string reason = "unsubscribe",
            CancellationToken ct = default)
        => await RequestAsync<JsonElement>("POST", "/v1/suppressions",
            new { email, reason }, ct);

    public async Task<JsonElement> UsageAsync(CancellationToken ct = default)
        => await RequestAsync<JsonElement>("GET", "/v1/usage", null, ct);

    private class BatchResponse
    {
        [JsonPropertyName("results")]
        public List<SendResult> Results { get; set; } = new();
    }

    private async Task<T> RequestAsync<T>(string method, string path, object? body,
            CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), _baseUrl + path);
            if (body != null)
                req.Content = JsonContent.Create(body);

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      and not SendFlitException)
            {
                throw new SendFlitException("network error: " + e.Message);
            }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<T>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

                var retryable = (int)res.StatusCode == 429 || (int)res.StatusCode >= 500;
                if (retryable && attempt < _maxRetries)
                {
                    attempt++;
                    var ra = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(300 << attempt);
                    await Task.Delay(ra + TimeSpan.FromMilliseconds(Random.Shared.Next(150)), ct);
                    continue;
                }

                string msg = $"SendFlit API error ({(int)res.StatusCode})";
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("detail", out var d)
                        && d.ValueKind == JsonValueKind.String)
                        msg = d.GetString()!;
                }
                catch { /* keep default */ }
                throw new SendFlitException(msg, (int)res.StatusCode, text);
            }
        }
    }

    /// <summary>Verify an X-Sendflit-Signature header against the raw body.</summary>
    public static bool VerifyWebhookSignature(string secret, byte[] rawBody, string? signature)
    {
        using var h = HMACSHA256.Create();
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(secret), rawBody);
        // (overload used above computes keyed hash; do explicit for clarity)
        var expected = "sha256=" + Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(rawBody)).ToLowerInvariant();
        return FixedTimeEquals(expected, signature ?? "");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        if (x.Length != y.Length) return false;
        int r = 0;
        for (int i = 0; i < x.Length; i++) r |= x[i] ^ y[i];
        return r == 0;
    }

    public void Dispose() => _http.Dispose();
}
