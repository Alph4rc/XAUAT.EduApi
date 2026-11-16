using Microsoft.AspNetCore.Mvc.Filters;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Filters;

public class EduCrawlerRateLimitFilter(
    IStudentRateLimitState rateLimitState,
    ICookieCodeService cookieCodeService) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var cookie = context.HttpContext.Request.GetEduAuthCookie();
        var cookieIdentity = HttpContextStudentExtensions.CreateCookieRateLimitIdentity(cookie);
        if (cookieIdentity is not null &&
            rateLimitState.TryGetBlockedUntil(cookieIdentity, out var cookieBlockedUntil))
        {
            throw new StudentCooldownException(cookieIdentity, cookieBlockedUntil);
        }

        var studentIds = await ResolveStudentIdsAsync(context, cookie, cookieIdentity);
        var rateLimitKeys = HttpContextStudentExtensions.CreateRateLimitStateKeys(studentIds);

        context.HttpContext.SetResolvedStudentIds(studentIds);
        context.HttpContext.SetResolvedRateLimitKeys(rateLimitKeys);

        foreach (var rateLimitKey in rateLimitKeys)
        {
            if (!rateLimitState.TryGetBlockedUntil(rateLimitKey, out var blockedUntil))
            {
                continue;
            }

            throw new StudentCooldownException(rateLimitKey, blockedUntil);
        }

        await next();
    }

    private async Task<string[]> ResolveStudentIdsAsync(
        ActionExecutingContext context, string cookie, string? cookieIdentity)
    {
        var controllerName = context.Controller.GetType().Name;
        var identities = new List<string>();
        if (!string.IsNullOrWhiteSpace(cookieIdentity))
        {
            identities.Add(cookieIdentity);
        }

        if (context.ActionArguments.TryGetValue("studentId", out var studentIdArg))
        {
            identities.AddRange(HttpContextStudentExtensions.ParseStudentIds(studentIdArg as string));
            return identities.Distinct(StringComparer.Ordinal).ToArray();
        }

        if (controllerName == "ProgramController" &&
            context.ActionArguments.TryGetValue("id", out var idArg))
        {
            identities.AddRange(HttpContextStudentExtensions.ParseStudentIds(idArg as string));
            return identities.Distinct(StringComparer.Ordinal).ToArray();
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return identities.Distinct(StringComparer.Ordinal).ToArray();
        }

        try
        {
            var resolvedStudentIds = await cookieCodeService.GetCode(cookie);
            identities.AddRange(HttpContextStudentExtensions.ParseStudentIds(resolvedStudentIds));
        }
        catch (RateLimitException) when (cookieIdentity is not null)
        {
            var cooldown = rateLimitState.MarkRateLimited(cookieIdentity);
            throw new StudentCooldownException(cookieIdentity, DateTimeOffset.UtcNow.Add(cooldown));
        }
        catch
        {
        }

        return identities.Distinct(StringComparer.Ordinal).ToArray();
    }
}
