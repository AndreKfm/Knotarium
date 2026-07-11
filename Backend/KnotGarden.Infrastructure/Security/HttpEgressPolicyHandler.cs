using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Infrastructure.Security;

public sealed class HttpEgressPolicyHandler : DelegatingHandler
{
    private readonly HttpEgressPolicyEvaluator _evaluator;

    public HttpEgressPolicyHandler(HttpEgressPolicyEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null)
        {
            throw new InvalidOperationException("Outbound request URI is required.");
        }

        _evaluator.EnsureAllowed(request.RequestUri);
        return base.SendAsync(request, cancellationToken);
    }
}