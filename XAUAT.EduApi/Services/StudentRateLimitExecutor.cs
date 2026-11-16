using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public class StudentRateLimitExecutor(
    IStudentRateLimitState rateLimitState) : IStudentRateLimitExecutor
{
    public async Task<T> ExecuteAsync<T>(IEnumerable<string?> studentIds, Func<Task<T>> action)
    {
        var normalizedKeys = NormalizeRateLimitKeys(studentIds);

        foreach (var rateLimitKey in normalizedKeys)
        {
            if (rateLimitState.TryGetBlockedUntil(rateLimitKey, out var blockedUntil))
            {
                throw new StudentCooldownException(rateLimitKey, blockedUntil);
            }
        }

        try
        {
            // 进入时没有有效冷却。并发请求可能在本次执行期间触发冷却，
            // 因此本次成功不能清除其他请求刚写入的状态。
            return await action();
        }
        catch (RateLimitException)
        {
            foreach (var rateLimitKey in normalizedKeys)
            {
                rateLimitState.MarkRateLimited(rateLimitKey);
            }

            throw;
        }
    }

    public async Task ExecuteAsync(IEnumerable<string?> studentIds, Func<Task> action)
    {
        await ExecuteAsync(studentIds, async () =>
        {
            await action();
            return true;
        });
    }

    private static string[] NormalizeRateLimitKeys(IEnumerable<string?> studentIds)
    {
        return HttpContextStudentExtensions.CreateRateLimitStateKeys(studentIds);
    }
}
