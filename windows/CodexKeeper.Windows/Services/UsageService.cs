using System.Text.Json.Nodes;
using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

public sealed class UsageService(SettingsStore store)
{
    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var settings = store.Snapshot();
        var executable = CodexLocator.Find(settings);
        await using var client = await CodexAppServerClient.ConnectAsync(executable, store.CodexHome, usageOnly: true, cancellationToken);
        var accountRead = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, cancellationToken);
        var accountType = accountRead["account"]?["type"]?.ToString();
        if (accountType != "chatgpt") throw new InvalidOperationException(Localization.Text("activity.account"));
        var first = await ReadRateLimitsAsync(client, cancellationToken);
        var snapshot = first;
        if (first.FiveHour is { UsedPercent: 0 } five && five.ResetsAt > first.CapturedAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2.2), cancellationToken);
            var second = await ReadRateLimitsAsync(client, cancellationToken);
            if (!StringComparer.Ordinal.Equals(first.AccountId, second.AccountId))
                throw new InvalidOperationException(Localization.Text("activity.authChanged"));
            var active = second.FiveHour is { UsedPercent: 0 } current && Math.Abs((current.ResetsAt - five.ResetsAt).TotalSeconds) <= 1;
            snapshot = second with { ZeroUseWindowActive = active };
        }
        return snapshot;
    }

    public async Task<PingModel> FindKeepAliveModelAsync(CancellationToken cancellationToken = default)
    {
        var settings = store.Snapshot();
        var executable = CodexLocator.Find(settings);
        await using var client = await CodexAppServerClient.ConnectAsync(executable, store.CodexHome, usageOnly: true, cancellationToken);
        var account = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, cancellationToken);
        if (account["account"]?["type"]?.ToString() != "chatgpt") throw new InvalidOperationException(Localization.Text("activity.account"));

        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageIndex = 0; pageIndex < 10; pageIndex++)
        {
            var parameters = new JsonObject { ["limit"] = 20, ["includeHidden"] = false };
            if (cursor is not null) parameters["cursor"] = cursor;
            var page = await client.RequestAsync("model/list", parameters, cancellationToken);
            if (page["data"] is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                {
                    if ((row["model"]?.ToString() ?? row["id"]?.ToString()) != "gpt-6-luna" || row["hidden"]?.GetValue<bool>() == true) continue;
                    var efforts = (row["supportedReasoningEfforts"] as JsonArray)?.OfType<JsonObject>()
                        .Select(item => item["reasoningEffort"]?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.Ordinal);
                    var lowSupported = efforts?.Contains("low") == true;
                    var declared = row["defaultReasoningEffort"]?.ToString();
                    var preferred = declared is not null && (efforts?.Contains(declared) ?? true) ? declared : null;
                    return new PingModel("gpt-6-luna", lowSupported ? "low" : preferred);
                }
            }
            cursor = page["nextCursor"]?.ToString();
            if (string.IsNullOrWhiteSpace(cursor)) break;
            if (!cursors.Add(cursor)) throw new InvalidDataException("Codex returned a repeated model cursor.");
        }
        throw new InvalidOperationException(Localization.Text("activity.noModel"));
    }

    private async Task<QuotaSnapshot> ReadRateLimitsAsync(CodexAppServerClient client, CancellationToken cancellationToken)
    {
        var response = await client.RequestAsync("account/rateLimits/read", new JsonObject(), cancellationToken);
        var accountId = response["accountId"]?.ToString();
        var fileAccount = ReadAuthAccountId(store.CodexHome);
        if (accountId is not null && fileAccount is not null && !StringComparer.Ordinal.Equals(accountId, fileAccount))
            throw new InvalidOperationException(Localization.Text("activity.authChanged"));
        accountId ??= fileAccount;
        if (string.IsNullOrWhiteSpace(accountId)) throw new InvalidOperationException(Localization.Text("activity.account"));
        var codex = response["rateLimitsByLimitId"]?["codex"] as JsonObject ?? response["rateLimits"] as JsonObject;
        if (codex is null) throw new InvalidDataException("Codex did not return quota windows.");
        QuotaWindow? five = null;
        QuotaWindow? weekly = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (codex[key] is not JsonObject window) continue;
            var used = ReadNumber(window["usedPercent"] ?? window["used_percent"]);
            var minutes = ReadNumber(window["windowDurationMins"] ?? window["window_minutes"]);
            var reset = ReadNumber(window["resetsAt"] ?? window["resets_at"]);
            if (used is null || minutes is null || reset is null || used < 0 || !double.IsFinite(used.Value)) continue;
            var parsed = new QuotaWindow(used.Value, (int)minutes.Value, DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(reset.Value * 1000))));
            if (parsed.WindowMinutes is >= 240 and <= 360) five = parsed;
            if (parsed.WindowMinutes is >= 8000 and <= 12000) weekly = parsed;
        }
        if (five is null && weekly is null) throw new InvalidDataException("Codex did not return recognized quota windows.");
        return new QuotaSnapshot(five, weekly, DateTimeOffset.UtcNow, accountId);
    }

    public static string? ReadAuthAccountId(string codexHome)
    {
        try
        {
            var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(codexHome, "auth.json")));
            var id = auth?["tokens"]?["account_id"]?.ToString();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch { return null; }
    }

    private static double? ReadNumber(JsonNode? node)
    {
        if (node is null) return null;
        if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
        return null;
    }
}

public sealed record PingModel(string Model, string? ReasoningEffort);
