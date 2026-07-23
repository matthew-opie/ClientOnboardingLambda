using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using ClientOnboardingLambda.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ClientOnboardingLambda;

public class Function
{
    public async Task FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext context)
    {
        var config = AppConfig.Load();
        var router = RequestRouter.Create(config);

        if (IsStreamQueryRoute(request))
        {
            await router.RouteStreamAsync(request, context, CancellationToken.None);
            return;
        }

        var response = await router.RouteAsync(request, context.Logger, CancellationToken.None);
        await HttpResponseStreamWriter.WriteAsync(response, CancellationToken.None);
    }

    private static bool IsStreamQueryRoute(APIGatewayHttpApiV2ProxyRequest request)
    {
        var method = request.RequestContext.Http.Method?.ToUpperInvariant() ?? "GET";
        var path = request.RawPath?.TrimEnd('/') ?? "/";
        return method == "POST" &&
               path.StartsWith("/tenants/", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith("/query/stream", StringComparison.OrdinalIgnoreCase);
    }
}
