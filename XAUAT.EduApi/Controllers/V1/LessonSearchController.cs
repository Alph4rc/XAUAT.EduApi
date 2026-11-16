using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>按需查询当前学期的全校教学班。</summary>
[ApiController]
[Route("v1/lesson-search")]
[Produces("application/json")]
[ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
[EnableRateLimiting("EduCrawler")]
public class LessonSearchController(ILessonSearchService lessons, ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer, ILogger<LessonSearchController> logger)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <param name="studentId">教务系统页面 URL 中的数字学生 ID。</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<LessonSearchItem>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<List<LessonSearchItem>>>> Get(string studentId)
    {
        try
        {
            var result = await lessons.GetLessonsAsync(studentId, Request.GetEduAuthCookie(), Language, HttpContext.RequestAborted);
            return Ok(SuccessListResponse(result));
        }
        catch (ArgumentException ex) { return BadRequest(ErrorResponse(ApiCodes.ParamError, ex.Message)); }
        catch (UnAuthenticationError) { return Unauthorized(ErrorResponse(ApiCodes.AuthFailed, Message(ApiMessageKey.AuthenticationFailed))); }
        catch (StudentCooldownException) { return RateLimited(ApiMessageKey.EduSystemRateLimited); }
        catch (RateLimitException) { return RateLimited(ApiMessageKey.EduSystemRateLimited); }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "查询全校教学班失败");
            return StatusCode(StatusCodes.Status502BadGateway, ErrorResponse(ApiCodes.UpstreamError, "教务系统开课查询失败"));
        }
    }
}
