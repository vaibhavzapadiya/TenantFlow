using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TenantFlow.Api.ErrorHandling;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Api.Tests.ErrorHandling;

/// <summary>
/// ADR-0016: the codes for framework-level failures are as stable as module codes. Every status a client
/// can see maps to one fixed code, and an unlisted status falls back to a generic code for its class.
/// </summary>
public sealed class HttpProblemCodesTests
{
    private static readonly Regex CodePattern = new(AppError.CodePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData(400, "request.malformed")]
    [InlineData(401, "authentication.required")]
    [InlineData(403, "authorization.forbidden")]
    [InlineData(404, "resource.not_found")]
    [InlineData(405, "request.method_not_allowed")]
    [InlineData(408, "request.timeout")]
    [InlineData(409, "request.conflict")]
    [InlineData(413, "request.payload_too_large")]
    [InlineData(415, "request.unsupported_media_type")]
    [InlineData(429, "request.rate_limited")]
    [InlineData(500, "server.unexpected")]
    [InlineData(418, "request.rejected")]
    [InlineData(503, "server.error")]
    public void Each_status_maps_to_its_fixed_code(int status, string expectedCode)
    {
        var (code, title) = HttpProblemCodes.For(status);

        Assert.Equal(expectedCode, code);
        Assert.Matches(CodePattern, code);
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "request.payload_too_large")]
    [InlineData(StatusCodes.Status408RequestTimeout, "request.timeout")]
    public void A_framework_bad_request_exception_keeps_its_own_status_and_code(int status, string expectedCode)
    {
        var handled = ProblemMapper.TryCreate(new BadHttpRequestException("framework text must not leak", status), out var problem);

        Assert.True(handled);
        Assert.Equal(status, problem.Status);
        Assert.Equal(expectedCode, problem.Extensions["code"]);
        Assert.DoesNotContain("leak", problem.Title, StringComparison.Ordinal);
    }
}
