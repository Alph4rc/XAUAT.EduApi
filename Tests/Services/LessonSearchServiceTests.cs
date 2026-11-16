using System.Net;
using System.Text;
using EduApi.Data.Models;
using Moq;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class LessonSearchServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("__pstsid__=secret", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LessonSearchService Create(Handler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(LessonSearchService.ClientName)).Returns(() => new HttpClient(handler, false));
        var exams = new Mock<IExamService>();
        exams.Setup(e => e.GetThisSemester("__pstsid__=secret", "zh"))
            .ReturnsAsync(new SemesterItem { Value = "999" });
        return new LessonSearchService(factory.Object, exams.Object, NoOpStudentRateLimitExecutor.Instance);
    }

    [Fact]
    public async Task FetchesAllPagesAndMapsTeachersAndCapacity()
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("1%2C500", StringComparison.OrdinalIgnoreCase)
            ? Json("""{"data":[{"id":"1","code":"A","nameZh":"班级","limitCount":30,"stdCount":18,"course":{"code":"C1","nameZh":"课程"},"teacherAssignmentList":[{"person":{"nameZh":"张"},"role":"MINOR"}]}],"_page_":{"totalRows":2}}""")
            : Json("""{"data":[{"id":"2","limitCount":10,"stdCount":10}],"_page_":{"totalRows":2}}"""));
        var result = await Create(handler).GetLessonsAsync("123", "__pstsid__=secret", "zh", default);
        Assert.Equal(2, result.Count);
        Assert.Equal("课程", result[0].CourseName);
        Assert.Equal("张(辅)", result[0].Teacher);
        Assert.Equal(12, result[0].Remaining);
        Assert.Equal("999", result[0].SemesterId);
        Assert.All(handler.Requests, uri => Assert.Contains("/semester/999/search/123", uri.AbsolutePath));
    }

    [Fact]
    public async Task RepeatedPageIsRejected()
    {
        var handler = new Handler(_ => Json("""{"data":[{"id":"1","limitCount":30,"stdCount":18}],"_page_":{"totalRows":2}}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).GetLessonsAsync("123", "__pstsid__=secret", "zh", default));
    }

    [Fact]
    public async Task RedirectIsAuthenticationFailure()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        await Assert.ThrowsAsync<UnAuthenticationError>(() => Create(handler).GetLessonsAsync("123", "__pstsid__=secret", "zh", default));
    }

    [Fact]
    public async Task IncompletePageIsRejected()
    {
        var handler = new Handler(_ => Json("""{"data":[{"id":"1","limitCount":30}]}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).GetLessonsAsync("123", "__pstsid__=secret", "zh", default));
    }
}
