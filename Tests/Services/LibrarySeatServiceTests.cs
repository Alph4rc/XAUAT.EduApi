using System.Net;
using System.Text.Json;
using Moq;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class LibrarySeatServiceTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];
        public bool FailNext { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("authorization", out var values) ? values.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (FailNext) { FailNext = false; return new HttpResponseMessage(HttpStatusCode.BadGateway); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":1,\"data\":[]}") };
        }
    }

    private static LibrarySeatService Create(Handler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("LibrarySeatClient"))
            .Returns(() => new HttpClient(handler, false) { BaseAddress = new Uri("https://libspace.xauat.edu.cn/") });
        return new LibrarySeatService(factory.Object);
    }

    [Fact]
    public async Task PublicQueries_CacheByDateAndRoom_AndDoNotCacheFailures()
    {
        var handler = new Handler();
        using var service = Create(handler);
        var date = new DateOnly(2026, 11, 16);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetRoomsAsync(date, default)));
        await service.GetRoomsAsync(date.AddDays(1), default);
        await service.GetSeatsAsync("3", date, default);
        await service.GetSeatsAsync("3", date, default);
        handler.FailNext = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetSeatsAsync("4", date, default));
        await service.GetSeatsAsync("4", date, default);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task Bookings_ForwardEachTokenAndNeverUsePublicCache()
    {
        var handler = new Handler();
        using var service = Create(handler);
        await service.GetBookingsAsync("first", 1, 10, default);
        await service.GetBookingsAsync("second", 2, 20, default);
        Assert.Equal(new[] { "bearerfirst", "bearersecond" }, handler.Requests.Select(r => r.Authorization));
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("bearersecond", body.RootElement.GetProperty("authorization").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("page").GetInt32());
    }
}
