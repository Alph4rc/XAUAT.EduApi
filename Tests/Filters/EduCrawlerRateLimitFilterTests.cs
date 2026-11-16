using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Filters;

public class EduCrawlerRateLimitFilterTests
{
    [Fact]
    public async Task OnActionExecutionAsync_ShouldThrowCooldownException_WhenStudentIdIsBlocked()
    {
        var state = new StudentRateLimitState();
        state.MarkRateLimited(HttpContextStudentExtensions.CreateRateLimitStateKeys(["20230001"]).Single());
        var filter = new EduCrawlerRateLimitFilter(state, Mock.Of<ICookieCodeService>());
        var context = BuildContext(
            controller: new object(),
            arguments: new Dictionary<string, object?> { ["studentId"] = "20230001" });
        context.HttpContext.Request.Path = "/score";

        await Assert.ThrowsAsync<StudentCooldownException>(() =>
            filter.OnActionExecutionAsync(context, () => throw new NotImplementedException()));
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldResolveStudentIdFromCookie_WhenActionHasNoStudentId()
    {
        var state = new StudentRateLimitState();
        state.MarkRateLimited(HttpContextStudentExtensions.CreateRateLimitStateKeys(["20230001"]).Single());
        var cookieCodeService = new Mock<ICookieCodeService>();
        cookieCodeService.Setup(x => x.GetCode("test-cookie")).ReturnsAsync("20230001");
        var filter = new EduCrawlerRateLimitFilter(state, cookieCodeService.Object);
        var context = BuildContext(controller: new object());
        context.HttpContext.Request.Path = "/score";
        context.HttpContext.Request.Headers["xauat"] = "test-cookie";

        await Assert.ThrowsAsync<StudentCooldownException>(() =>
            filter.OnActionExecutionAsync(context, () => throw new NotImplementedException()));
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldContinue_WhenCookieParsingFails()
    {
        var cookieCodeService = new Mock<ICookieCodeService>();
        cookieCodeService.Setup(x => x.GetCode(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("parse failed"));
        var filter = new EduCrawlerRateLimitFilter(new StudentRateLimitState(), cookieCodeService.Object);
        var context = BuildContext(controller: new object());
        context.HttpContext.Request.Path = "/score";
        context.HttpContext.Request.Headers["xauat"] = "test-cookie";
        var executed = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult<ActionExecutedContext>(
                new ActionExecutedContext(context, [], context.Controller));
        });

        Assert.True(executed);
        var identities = context.HttpContext.GetResolvedStudentIds();
        var rateLimitKeys = context.HttpContext.GetResolvedRateLimitKeys();
        Assert.Single(identities);
        Assert.StartsWith("cookie:", identities.Single());
        Assert.Empty(rateLimitKeys);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldNotBlock_WhenStudentIdCannotBeResolved()
    {
        var cookie = "test-cookie";
        var state = new StudentRateLimitState();

        var cookieCodeService = new Mock<ICookieCodeService>();
        cookieCodeService.Setup(x => x.GetCode(cookie))
            .ThrowsAsync(new InvalidOperationException("parse failed"));

        var filter = new EduCrawlerRateLimitFilter(state, cookieCodeService.Object);
        var context = BuildContext(controller: new object());
        context.HttpContext.Request.Path = "/score";
        context.HttpContext.Request.Headers["xauat"] = cookie;
        var executed = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult<ActionExecutedContext>(
                new ActionExecutedContext(context, [], context.Controller));
        });

        Assert.True(executed);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldBlockCrossPath()
    {
        var cookie = "test-cookie";
        var state = new StudentRateLimitState();
        state.MarkRateLimited(HttpContextStudentExtensions.CreateRateLimitStateKeys(["20230001"]).Single());

        var cookieCodeService = new Mock<ICookieCodeService>();
        cookieCodeService.Setup(x => x.GetCode(cookie)).ReturnsAsync("20230001");

        var filter = new EduCrawlerRateLimitFilter(state, cookieCodeService.Object);
        var context = BuildContext(controller: new object());
        context.HttpContext.Request.Path = "/course";
        context.HttpContext.Request.Headers["xauat"] = cookie;

        await Assert.ThrowsAsync<StudentCooldownException>(() =>
            filter.OnActionExecutionAsync(context, () => throw new NotImplementedException()));
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldSkipFurtherLookups_WhenCookieLookupWasRateLimited()
    {
        const string cookie = "test-cookie";
        var cookieCodeService = new Mock<ICookieCodeService>();
        cookieCodeService.Setup(x => x.GetCode(cookie)).ThrowsAsync(new RateLimitException());
        var filter = new EduCrawlerRateLimitFilter(new StudentRateLimitState(), cookieCodeService.Object);

        foreach (var path in new[] { "/v1/info/completion", "/v1/score/thissemester" })
        {
            var context = BuildContext(controller: new object());
            context.HttpContext.Request.Path = path;
            context.HttpContext.Request.Headers["xauat"] = cookie;
            await Assert.ThrowsAsync<StudentCooldownException>(() =>
                filter.OnActionExecutionAsync(context, () => throw new NotImplementedException()));
        }

        cookieCodeService.Verify(x => x.GetCode(cookie), Times.Once);
    }

    [Fact]
    public void RequestRateLimitPartition_ShouldBeSharedAcrossPathsForTheSameSession()
    {
        var scores = new DefaultHttpContext().Request;
        scores.Path = "/v1/score";
        scores.QueryString = new QueryString("?studentId=20230001&semester=2");
        scores.Headers["xauat"] = "session-a";
        var progress = new DefaultHttpContext().Request;
        progress.Path = "/v1/info/completion";
        progress.Headers["xauat"] = "session-a";

        Assert.Equal(scores.CreateRequestRateLimitPartitionKey(), progress.CreateRequestRateLimitPartitionKey());
        progress.Headers["xauat"] = "session-b";
        Assert.NotEqual(scores.CreateRequestRateLimitPartitionKey(), progress.CreateRequestRateLimitPartitionKey());
    }

    private static ActionExecutingContext BuildContext(object controller, Dictionary<string, object?>? arguments = null)
    {
        var httpContext = new DefaultHttpContext();

        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            arguments ?? new Dictionary<string, object?>(),
            controller);
    }
}
