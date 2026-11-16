using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using System.Net;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class CookieCodeServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly CookieCodeService _service;

    public CookieCodeServiceTests()
    {
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _service = new CookieCodeService(_mockHttpClientFactory.Object);
    }

    [Fact]
    public void ParseCookie_ShouldReturnEmptyString_WhenCookiesAreEmpty()
    {
        // Arrange
        var cookies = new List<string>();

        // Act
        var result = _service.ParseCookie(cookies);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void ParseCookie_ShouldReturnFormattedCookie_WhenCookiesAreValid()
    {
        // Arrange
        var cookies = new List<string>
        {
            "__pstsid__=123",
            "SESSION=abc; path=/"
        };

        // Act
        var result = _service.ParseCookie(cookies);

        // Assert
        Assert.Contains("__pstsid__=123;", result);
        Assert.Contains("SESSION=abc;", result);
    }

    [Fact]
    public async Task GetCode_ShouldReturnCode_WhenResponseIsValid()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("<html><input value=\"123456\"></html>")
        };

        handlerMock
           .Protected()
           .Setup<Task<HttpResponseMessage>>(
              "SendAsync",
              ItExpr.IsAny<HttpRequestMessage>(),
              ItExpr.IsAny<CancellationToken>()
           )
           .ReturnsAsync(response);

        var client = new HttpClient(handlerMock.Object);
        _mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(client);

        // Act
        var result = await _service.GetCode("test_cookie");

        // Assert
        Assert.Equal("123456", result);
    }

    [Fact]
    public async Task GetCode_ShouldReuseCachedIdentityAcrossRequestScopes()
    {
        await using var services = new ServiceCollection().AddLogging().AddCacheServices().BuildServiceProvider();
        var cache = services.GetRequiredService<ICacheService>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><input value=\"123456\"></html>")
            });
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object, disposeHandler: false));

        var first = new CookieCodeService(_mockHttpClientFactory.Object, cache);
        var second = new CookieCodeService(_mockHttpClientFactory.Object, cache);
        Assert.Equal("123456", await first.GetCode("test-cookie"));
        Assert.Equal("123456", await second.GetCode("test-cookie"));
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetCode_ShouldThrowUnAuthenticationError_WhenResponseContainsLoginPage()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("<html>...登入页面...</html>")
        };

        handlerMock
           .Protected()
           .Setup<Task<HttpResponseMessage>>(
              "SendAsync",
              ItExpr.IsAny<HttpRequestMessage>(),
              ItExpr.IsAny<CancellationToken>()
           )
           .ReturnsAsync(response);

        var client = new HttpClient(handlerMock.Object);
        _mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(client);

        // Act & Assert
        await Assert.ThrowsAsync<XAUAT.EduApi.Exceptions.UnAuthenticationError>(() => _service.GetCode("test_cookie"));
    }
}
