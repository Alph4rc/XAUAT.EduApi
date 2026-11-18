using System.Globalization;
using EduApi.Data.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.WebUtilities;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;

namespace XAUAT.EduApi.Services;

public interface IElectricityService
{
    public Task<double?> FetchCurrentBalanceAsync(string? url = null);
    public Task<List<ElectricData>> FetchWeeklyDataAsync(string? url = null);
    public Task<string?> GetRechargeUrlAsync(string? url = null);
}

public sealed class ElectricityUrlException(bool isExpired)
    : Exception(isExpired ? "电费链接已过期，请从微信重新复制" : "请输入完整有效的电费链接")
{
    public bool IsExpired { get; } = isExpired;
}

public class ElectricityService(
    IHttpClientFactory httpClientFactory,
    ICacheService cacheService) : IElectricityService
{
    private static readonly TimeSpan BalanceCacheExpiration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WeeklyDataCacheExpiration = TimeSpan.FromMinutes(30);

    public async Task<double?> FetchCurrentBalanceAsync(string? url = null)
    {
        var source = ParseSourceUrl(url);
        if (source is null)
        {
            return null;
        }

        try
        {
            return await cacheService.GetOrCreateAsync(
                CacheKeys.ElectricityBalance(source.OriginalString),
                () => FetchBalanceFromRemoteAsync(source),
                BalanceCacheExpiration);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ElectricData>> FetchWeeklyDataAsync(string? url = null)
    {
        var source = ParseSourceUrl(url);
        if (source is null)
        {
            return [];
        }

        var detailUrl = WithAccountPage(source, "wxElecDtl");
        return await cacheService.GetOrCreateAsync(
            CacheKeys.ElectricityWeeklyData(source.OriginalString),
            () => FetchWeeklyDataFromRemoteAsync(detailUrl),
            WeeklyDataCacheExpiration, isUse: false);
    }

    public Task<string?> GetRechargeUrlAsync(string? url = null)
    {
        var source = ParseSourceUrl(url);
        return Task.FromResult(source is null ? null : WithAccountPage(source, "wxCharge").AbsoluteUri);
    }

    private static Uri? ParseSourceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var input = url.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(source.Host) || source.UserInfo.Length > 0 ||
            input.Any(c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ElectricityUrlException(isExpired: false);
        }

        var query = QueryHelpers.ParseQuery(source.Query);
        if (query.TryGetValue("exp", out var values))
        {
            if (values.Count != 1 ||
                !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expiration))
            {
                throw new ElectricityUrlException(isExpired: false);
            }

            if (expiration <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                throw new ElectricityUrlException(isExpired: true);
            }
        }

        return source;
    }

    private static Uri WithAccountPage(Uri source, string page)
    {
        var path = source.AbsolutePath;
        var pageStart = path.LastIndexOf('/') + 1;
        var accountPage = path[pageStart..];
        if (accountPage.Equals("wxAccount.aspx", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(source) { Path = path[..pageStart] + page + ".aspx" }.Uri;
        }

        if (accountPage.Equals("wxAccount", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(source) { Path = path[..pageStart] + page }.Uri;
        }

        return source;
    }

    private async Task<double?> FetchBalanceFromRemoteAsync(Uri resolvedUrl)
    {
        using var httpClient = httpClientFactory.CreateClient();

        using var response = await httpClient.GetAsync(resolvedUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync();
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var text = document.DocumentNode.InnerText;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));

        foreach (var line in lines)
        {
            const string keyword = "充值余额：¥";
            if (!line.Contains(keyword))
            {
                continue;
            }

            var balanceText = line.Split(keyword)[1].Trim();
            if (double.TryParse(balanceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var balance))
            {
                return balance;
            }
        }

        return null;
    }

    private async Task<List<ElectricData>> FetchWeeklyDataFromRemoteAsync(Uri detailUrl)
    {
        using var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.GetAsync(detailUrl);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var tables = document.DocumentNode.SelectNodes("//table");
        if (tables is null)
        {
            // 上游页面没有 table：常见于登录失效返回的错误页或页面结构变化。
            // 返回空集合，避免在下面的 foreach 上抛 NullReferenceException。
            return [];
        }

        var data = new List<ElectricData>();
        foreach (var table in tables)
        {
            var rows = table.SelectNodes(".//tr");
            if (rows is null)
            {
                continue;
            }

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td");
                if (cells is not { Count: 3 })
                {
                    continue;
                }

                var timestamp = ParseTimestamp(cells[1].InnerText.Trim());
                var valueText = cells[2].InnerText.Trim();
                if (timestamp == null ||
                    !double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                // 按"整点"分桶：ParseTimestamp 已把时间戳对齐到小时，
                // 所以整段时间戳相等就代表同一个小时（跨天也不会误并）。
                if (data.Count == 0 || data[^1].Timestamp != timestamp.Value)
                {
                    data.Add(new ElectricData()
                    {
                        Timestamp = timestamp.Value, Value = value
                    });
                }
                else
                {
                    data[^1].Value += value;
                }
            }
        }

        data.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return data;
    }

    private static DateTime? ParseTimestamp(string rawValue)
    {
        if (!DateTime.TryParse(rawValue, out var timestamp))
        {
            return null;
        }

        // 周用电是**按小时聚合**的：时间戳必须落到整点，否则同一小时的多条记录
        // 会带着各自的分秒散落在曲线上（上游给的是 08:12 这种带分钟的原始时刻）。
        return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0);
    }
}