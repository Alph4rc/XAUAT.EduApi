using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

public record LessonSearchItem(string SectionId, string CourseCode, string CourseName, string SectionCode,
    string SectionName, string Teacher, string TimeText, string Location, string SemesterId,
    string SemesterName, string BizTypeId, int Capacity, int SelectedCount, int? ReserveCount,
    int? RetakeCount, int? CourseSelectedCount, int Remaining);

public interface ILessonSearchService
{
    Task<List<LessonSearchItem>> GetLessonsAsync(string studentId, string cookie, string language, CancellationToken cancellationToken);
}

public sealed class LessonSearchService(IHttpClientFactory clients, IExamService exams,
    IStudentRateLimitExecutor rateLimit) : ILessonSearchService
{
    public const string ClientName = "LessonSearch";
    private const int PageSize = 500;
    private const int MaxPages = 500;

    public async Task<List<LessonSearchItem>> GetLessonsAsync(string studentId, string cookie, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(studentId) || !studentId.All(char.IsAsciiDigit))
            throw new ArgumentException("studentId 必须是教务系统的数字学生 ID");
        if (string.IsNullOrWhiteSpace(cookie)) throw new UnAuthenticationError();
        var identity = HttpContextStudentExtensions.CreateCookieRateLimitIdentity(cookie);
        return await rateLimit.ExecuteAsync([studentId, identity], async () =>
        {
            var semester = await exams.GetThisSemester(cookie, language);
            if (string.IsNullOrWhiteSpace(semester.Value) || !semester.Value.All(char.IsAsciiDigit))
                throw new HttpRequestException("无法确定当前学期");
            using var client = clients.CreateClient(ClientName);
            client.Timeout = HttpTimeouts.EduSystem;
            var result = new List<LessonSearchItem>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int? total = null;
            for (var page = 1; page <= MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"https://swjw.xauat.edu.cn/student/for-std/lesson-search/semester/{semester.Value}/search/{studentId}?bizTypeAssoc=1&queryPage__={page}%2C{PageSize}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                using var response = await EduSystemThrottler.ThrottleAsync(
                    () => client.SendAsync(request, cancellationToken), cancellationToken);
                response.ThrowIfRateLimited();
                if ((int)response.StatusCode is >= 300 and < 400 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnAuthenticationError();
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                content.ThrowIfAuthOrRateLimited();
                if (content.Contains("authserver", StringComparison.OrdinalIgnoreCase) && content.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    throw new UnAuthenticationError();
                JsonNode root;
                try { root = JsonNode.Parse(content) ?? throw new JsonException(); }
                catch (JsonException ex) { throw new HttpRequestException("开课查询返回了无效的 JSON", ex); }
                if (root is not JsonObject obj || obj["data"] is not JsonArray rows)
                    throw new HttpRequestException("开课查询响应格式发生变化");
                var reported = Number((obj["_page_"] as JsonObject)?["totalRows"]);
                if (reported is < 0 || (total.HasValue && reported.HasValue && total != reported))
                    throw new HttpRequestException("开课查询总数在分页期间发生变化");
                total ??= reported;
                if (rows.Count == 0)
                {
                    if (result.Count == 0 || (total.HasValue && result.Count != total.Value)) throw new HttpRequestException("开课查询结果为空或不完整");
                    return result;
                }
                var before = result.Count;
                foreach (var node in rows)
                {
                    if (node is not JsonObject row) throw new HttpRequestException("教学班数据格式无效");
                    var item = Map(row, semester.Value);
                    if (!ids.Add(item.SectionId)) throw new HttpRequestException("开课查询包含重复教学班，分页结果不可靠");
                    result.Add(item);
                }
                if (result.Count == before || (total.HasValue && result.Count > total))
                    throw new HttpRequestException("开课查询分页结果不可靠");
                if (total.HasValue && result.Count == total) return result;
            }
            throw new HttpRequestException("开课查询超过最大分页数");
        });
    }

    private static LessonSearchItem Map(JsonObject row, string semesterId)
    {
        var id = Text(row, "id", "lessonId", "lesson_id", "teachingClassId", "teachingClassID", "classId");
        var capacity = Number(First(row, "limitCount", "capacity", "maxCount"));
        var selected = Number(First(row, "stdCount", "selectedCount", "studentCount"));
        if (string.IsNullOrEmpty(id) || capacity is null || selected is null)
            throw new HttpRequestException("教学班缺少 ID、容量或已选人数");
        var course = row["course"] as JsonObject;
        var semester = row["semester"] as JsonObject;
        var biz = (row["bizType"] ?? row["biz_type"]) as JsonObject;
        var sectionName = Text(row, "teachingClassName", "className", "nameZh", "name");
        var courseName = Text(row, "courseName", "courseNameZh", "lessonNameZh", "lessonName");
        if (courseName == "") courseName = Text(course, "nameZh", "name", "courseName");
        if (courseName == "") courseName = sectionName;
        if (sectionName == "") sectionName = courseName;
        var teacher = Text(row, "teacherNames", "teacherName", "teachersText");
        if (teacher == "" && row["teacherAssignmentList"] is JsonArray assignments)
            teacher = string.Join(", ", assignments.OfType<JsonObject>().Select(a =>
            {
                var person = a["person"] as JsonObject ?? (a["teacher"] as JsonObject)?["person"] as JsonObject ?? a["teacher"] as JsonObject;
                var name = Text(person, "nameZh", "name", "nameEn");
                var role = Text(a, "role").ToUpperInvariant();
                return name == "" ? "" : name + (role == "MINOR" ? "(辅)" : role == "ASSISTANT" ? "(助)" : "");
            }).Where(s => s != "").Distinct());
        if (teacher == "") teacher = Text(row, "teacherAssignmentStr");
        if (teacher == "" && row["teachers"] is JsonArray teachers)
            teacher = string.Join(", ", teachers.Select(t => t is JsonObject o ? Text(o, "nameZh", "name", "teacherName") : t?.ToString() ?? "").Where(s => s != "").Distinct());
        if (teacher == "") teacher = row["teacher"] is JsonObject t ? Text(t, "nameZh", "name") : Text(row, "teacher");
        return new(id, Text(row, "courseCode", "lessonCode") is { Length: > 0 } code ? code : Text(course, "code", "courseCode"), courseName,
            Text(row, "teachingClassCode", "classCode", "sectionCode", "code"), sectionName, teacher,
            Text(row, "timeText", "scheduleText", "activityText", "lessonTime"), Text(row, "location", "place", "room", "classroom"),
            Text(row, "semesterId") is { Length: > 0 } sid ? sid : Text(semester, "id") is { Length: > 0 } nestedSid ? nestedSid : semesterId,
            Text(row, "semesterName") is { Length: > 0 } sn ? sn : Text(semester, "nameZh", "name"),
            Text(row, "bizTypeId") is { Length: > 0 } bid ? bid : Text(biz, "id"), capacity.Value, selected.Value,
            Number(row["reserveCount"]), Number(row["retakeCount"]), Number(row["courseStdCount"]),
            Number(First(row, "remainCount", "remainingCount", "remain")) ?? Math.Max(capacity.Value - selected.Value, 0));
    }
    private static JsonNode? First(JsonObject? row, params string[] keys) => keys.Select(k => row?[k]).FirstOrDefault(n => n is not null);
    private static string Text(JsonObject? row, params string[] keys) => First(row, keys)?.ToString().Trim() ?? "";
    private static int? Number(JsonNode? node) => int.TryParse(node?.ToString().Replace(",", ""), out var n) ? n : null;
}
