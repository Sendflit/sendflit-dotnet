# SendFlit — .NET

Transactional email for applications and AI agents. Zero dependencies (System.Text.Json + HttpClient), .NET 6+.

## Install

```
dotnet add package SendFlit
```

## Usage

```csharp
using SendFlit;

await using var sf = new Client("re_...");

var res = await sf.SendAsync(new Email
{
    From = "you@yourdomain.com",
    To = "user@example.com",
    Subject = "Confirm your email",
    Html = "<p>Click <a href='...'>here</a>.</p>",
});
Console.WriteLine($"{res.Id} {res.Status}");
```

### Attachments / batch / webhook

```csharp
var email = new Email { From = "billing@yourdomain.com", To = "u@example.com", Subject = "Receipt" };
email.Attach("receipt.pdf", pdfBytes, "application/pdf");

await sf.BatchAsync(new List<Email> { emailA, emailB });

Client.VerifyWebhookSignature(secret, rawBody, signatureHeader);
```

Async throughout (`CancellationToken` on every call); retries 429/5xx honoring `Retry-After`;
errors throw `SendFlitException` with `.Status`/`.Body`.

MIT © SendFlit
