using System.Text;
using System.Text.RegularExpressions;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public partial class CookieCodeService(
    IHttpClientFactory httpClientFactory,
    ICacheService? cacheService = null) : ICookieCodeService
{
    public async Task<string> GetCode(string cookies)
    {
        var identity = HttpContextStudentExtensions.CreateCookieRateLimitIdentity(cookies);
        if (identity is null || cacheService is null)
        {
            return await FetchCode(cookies);
        }

        var code = await cacheService.GetOrCreateAsync<string?>(
            $"eduapi:student-id:{identity}",
            async () =>
            {
                var result = await FetchCode(cookies);
                return string.IsNullOrWhiteSpace(result) || result.StartsWith('/') ? null : result;
            },
            TimeSpan.FromMinutes(20),
            CacheLevel.Local);
        return code ?? "";
    }

    private async Task<string> FetchCode(string cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://swjw.xauat.edu.cn/student/for-std/precaution");
        request.Headers.Add("Cookie", cookies);

        using var httpClient = httpClientFactory.CreateClient("DefaultClient");
        using var response = await httpClient.SendAsync(request);
        response.ThrowIfRateLimited();

        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        var a = request.RequestUri!.LocalPath.Replace("/student/precaution/index/", "");

        if (a != "/student/for-std/precaution") return a;
        var content = await response.Content.ReadAsStringAsync();

        content.ThrowIfAuthOrRateLimited();

        var matches = MyRegex().Matches(content);
        return matches.Count >= 1 ? string.Join(',', matches.Select(m => m.Groups[1].Value)) : "";
    }

    public string ParseCookie(IEnumerable<string> cookies)
    {
        var result = new StringBuilder();

        foreach (var cookie in cookies)
        {
            if (cookie.Contains("__pstsid__"))
            {
                result.Append(cookie).Append(';');
            }
            else if (cookie.Contains("SESSION"))
            {
                var sessionValue = cookie.Split('=')[1].Split(';')[0];
                result.Append("SESSION=").Append(sessionValue).Append(';');
            }
        }

        return result.ToString();
    }

    [GeneratedRegex("""value="(.*?)">""")]
    private static partial Regex MyRegex();
}