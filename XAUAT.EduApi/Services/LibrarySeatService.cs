using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace XAUAT.EduApi.Services;

/// <summary>图书馆公开座位数据和个人预约记录。</summary>
public sealed class LibrarySeatService(IHttpClientFactory clients) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512 });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    public Task<JsonElement> GetRoomsAsync(DateOnly date, CancellationToken ct) =>
        PublicAsync($"rooms:{date:yyyy-MM-dd}", async token =>
        {
            var (start, end) = Window();
            return await PostAsync("reserve/index/list", new Dictionary<string, object>
            {
                ["id"] = "1", ["date"] = date.ToString("yyyy-MM-dd"), ["premisesIds"] = "",
                ["categoryIds"] = "", ["storeyIds"] = "", ["noiseId"] = "", ["boutiqueIds"] = "",
                ["startTime"] = start, ["endTime"] = end, ["members"] = "", ["size"] = "100", ["page"] = "1"
            }, null, token);
        }, ct);

    public Task<JsonElement> GetSeatsAsync(string roomId, DateOnly date, CancellationToken ct) =>
        PublicAsync($"seats:{date:yyyy-MM-dd}:{roomId}", async token =>
        {
            var (start, end) = Window();
            return await PostAsync("api/Seat/seat", new Dictionary<string, object>
            {
                ["area"] = roomId, ["segment"] = "1", ["day"] = date.ToString("yyyy-MM-dd"),
                ["startTime"] = start, ["endTime"] = end
            }, null, token);
        }, ct);

    public Task<JsonElement> GetBookingsAsync(string token, int page, int limit, CancellationToken ct) =>
        PostAsync("api/Member/seat", new Dictionary<string, object> { ["page"] = page, ["limit"] = limit }, token, ct);

    private async Task<JsonElement> PublicAsync(string key, Func<CancellationToken, Task<JsonElement>> fetch, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out JsonElement cached)) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;
            var result = await fetch(ct);
            _cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheLifetime, Size = 1 });
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement> PostAsync(string path, Dictionary<string, object> body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (token is not null)
        {
            var authorization = "bearer" + token;
            request.Headers.TryAddWithoutValidation("authorization", authorization);
            body["authorization"] = authorization;
        }
        request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient("LibrarySeatClient").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new HttpRequestException("图书馆响应格式错误");
        if (root.TryGetProperty("code", out var code))
        {
            var value = code.ToString();
            if (value == "10001") throw new LibraryAuthenticationException();
            if (value is not ("0" or "1" or "")) throw new HttpRequestException("图书馆接口返回错误");
        }
        return root.Clone();
    }

    private static (string Start, string End) Window()
    {
        var now = SchoolClock.Now;
        var end = now.AddHours(1);
        return (now.ToString("HH:mm"), end.Date == now.Date ? end.ToString("HH:mm") : "23:59");
    }

    public void Dispose() { _cache.Dispose(); _gate.Dispose(); }
}

public sealed class LibraryAuthenticationException : Exception;
