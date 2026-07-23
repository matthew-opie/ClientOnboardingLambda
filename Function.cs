using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using ClientOnboardingLambda.Models;
using ClientOnboardingLambda.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ClientOnboardingLambda;

public class Function
{
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"Route: {request.RequestContext.Http.Method} {request.RawPath}");

        var config = AppConfig.Load();
        var router = RequestRouter.Create(config);
        return await router.RouteAsync(request, context.Logger, CancellationToken.None);
    }
}

[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(TenantListItemDto))]
public partial class LambdaJsonContext : JsonSerializerContext;
