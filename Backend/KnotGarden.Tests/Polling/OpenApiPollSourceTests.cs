using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Features.Polling;
using Xunit;

namespace KnotGarden.Tests.Polling;

public class OpenApiPollSourceTests
{
    private sealed class StubInvoker : IOpenApiOperationInvoker
    {
        private readonly OpenApiPollResponse _response;
        public StubInvoker(OpenApiPollResponse response) => _response = response;
        public Task<OpenApiPollResponse> InvokeAsync(string s, string o, string? v, CancellationToken ct) =>
            Task.FromResult(_response);
    }

    [Fact]
    public async Task Hash_DetectsChangeOverOperationBody()
    {
        var source = new OpenApiPollSource(new StubInvoker(new OpenApiPollResponse("{\"v\":1}", null, null)));
        var config = "{\"changeDetection\":\"hash\",\"serverConfigId\":\"srv-1\",\"operationId\":\"listItems\"}";

        var first = await source.PollAsync(new PollContext(config, null), CancellationToken.None);
        var second = await source.PollAsync(new PollContext(config, first.NewCursor), CancellationToken.None);

        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
    }

    [Fact]
    public async Task Etag_UsesResponseEtag()
    {
        var source = new OpenApiPollSource(new StubInvoker(new OpenApiPollResponse("{\"v\":1}", "\"e1\"", null)));
        var config = "{\"changeDetection\":\"etag\",\"serverConfigId\":\"srv-1\",\"operationId\":\"listItems\"}";

        var result = await source.PollAsync(new PollContext(config, null), CancellationToken.None);

        Assert.True(result.HasNew);
        Assert.Equal("\"e1\"", result.NewCursor);
    }
}
