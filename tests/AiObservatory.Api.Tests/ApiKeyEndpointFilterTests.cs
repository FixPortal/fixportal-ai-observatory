using System.Security.Claims;
using AiObservatory.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiObservatory.Api.Tests;

public class ApiKeyEndpointFilterTests
{
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly ApiKeyEndpointFilter _sut;

    public ApiKeyEndpointFilterTests()
    {
        // Default to a non-dev environment; the keyless-GET test overrides this per case.
        _env.EnvironmentName.Returns(Environments.Production);
        _sut = new ApiKeyEndpointFilter(_config, _env);
    }

    [Theory]
    // Keyless GET is allowed only in Development; any other environment fails closed (503).
    [InlineData("GET", "Development", true, 200)]
    [InlineData("GET", "Production", false, 503)]
    [InlineData("POST", "Production", false, 503)]
    public async Task InvokeAsync_WhenKeysNotConfigured_FailsClosedOutsideDev(
        string method, string environment, bool expectNext, int expectedStatus)
    {
        // Arrange
        _env.EnvironmentName.Returns(environment);
        _config["OBSERVATORY_API_KEY"].Returns((string?)null);
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.Equal(expectNext, nextCalled);
        if (expectNext)
        {
            Assert.IsType<Ok>(result);
        }
        else
        {
            var statusResult = Assert.IsType<StatusCodeHttpResult>(result);
            Assert.Equal(expectedStatus, statusResult.StatusCode);
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyUnconfigured_AllowsValidAdminKey()
    {
        // Arrange — OBSERVATORY_READONLY_API_KEY unset, OBSERVATORY_API_KEY set, Production.
        // A valid admin key on a GET must still be reachable even though the readonly
        // key was never configured (e.g. the Activity endpoints in a deploy that hasn't
        // set OBSERVATORY_READONLY_API_KEY).
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "admin-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.IsType<Ok>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyUnconfigured_RejectsWrongKey()
    {
        // Arrange — same setup, but the header key doesn't match the admin key. Because
        // an admin key IS configured, a mismatch is a plain 401, not the 503 misconfig
        // fallback (that fallback only fires when no key is configured at all).
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "wrong-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_AllowsValidReadOnlyKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "readonly-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.IsType<Ok>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_AllowsValidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "admin-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.IsType<Ok>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_RejectsInvalidKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "wrong-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_RejectsWrongLengthKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Headers["X-Observatory-Key"] = "short"; // wrong length
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task InvokeAsync_WhenUserAuthenticated_AllowsRegardlessOfKeys(string method)
    {
        // Arrange — keys configured, but no key header supplied. An Entra-authenticated
        // user (JWT bearer) must be allowed through for both reads and writes.
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "chris")], authenticationType: "Bearer")),
        };
        httpContext.Request.Method = method;
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.IsType<Ok>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_AllowsValidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers["X-Observatory-Key"] = "admin-key-12345";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.True(nextCalled);
        Assert.IsType<Ok>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_RejectsInvalidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers["X-Observatory-Key"] = "wrong-admin-key";
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_RejectsWrongLengthAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers["X-Observatory-Key"] = "short"; // wrong length
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }
}
