using Microsoft.Extensions.Configuration;

namespace AiObservatory.Api.Services;

public sealed class WebhookAlertNotifier(IHttpClientFactory httpClientFactory, IConfiguration config)
    : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var url = config["BUDGET_ALERT_WEBHOOK_URL"];
        if (string.IsNullOrEmpty(url)) return;
        var client = httpClientFactory.CreateClient();
        await client.PostAsJsonAsync(url, payload, ct);
    }
}
