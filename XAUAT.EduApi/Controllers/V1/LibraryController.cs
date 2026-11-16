using System.Text.Json;
using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>图书馆公开座位及个人预约查询。公开数据缓存 20 秒。</summary>
[ApiController]
[Route("v1/library")]
[Produces("application/json")]
[EnableRateLimiting("EduCrawler")]
public sealed class LibraryController(
    LibrarySeatService service,
    ILogger<LibraryController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer) : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>查询区域及空位数量；日期默认学校当地今天。</summary>
    [HttpGet("rooms")]
    public Task<IActionResult> Rooms([FromQuery] DateOnly? date, CancellationToken ct) =>
        Execute(() => service.GetRoomsAsync(date ?? DateOnly.FromDateTime(SchoolClock.Now), ct), ct);

    /// <summary>查询区域座位；日期默认学校当地今天，时间窗口为当前时刻起一小时。</summary>
    [HttpGet("rooms/{roomId}/seats")]
    public Task<IActionResult> Seats(string roomId, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (roomId.Length > 64 || !roomId.All(char.IsAsciiDigit))
            return Task.FromResult<IActionResult>(BadRequest(ErrorResponse(ApiCodes.ParamError, "区域 ID 格式错误")));
        return Execute(() => service.GetSeatsAsync(roomId, date ?? DateOnly.FromDateTime(SchoolClock.Now), ct), ct);
    }

    /// <summary>查询个人座位预约；X-Library-Token 为图书馆 H5 的 JWT，不含 bearer 前缀。</summary>
    [HttpGet("bookings")]
    public Task<IActionResult> Bookings([FromHeader(Name = "X-Library-Token")] string? token,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsWhiteSpace))
            return Task.FromResult<IActionResult>(BadRequest(ErrorResponse(ApiCodes.ParamError, "需要有效的图书馆 token")));
        if (page < 1 || pageSize is < 1 or > 200)
            return Task.FromResult<IActionResult>(BadRequest(ErrorResponse(ApiCodes.ParamError, "分页参数无效")));
        Response.Headers.CacheControl = "no-store";
        return Execute(() => service.GetBookingsAsync(token, page, pageSize, ct), ct);
    }

    private async Task<IActionResult> Execute(Func<Task<JsonElement>> operation, CancellationToken ct)
    {
        try { return Ok(SuccessResponse(await operation())); }
        catch (LibraryAuthenticationException)
        {
            return Unauthorized(ErrorResponse(ApiCodes.AuthFailed, "图书馆登录已失效"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning("图书馆上游请求失败：{Type}", ex.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway, ErrorResponse(ApiCodes.UpstreamError, "图书馆服务暂不可用"));
        }
    }
}
