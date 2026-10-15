using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer;

public static class HttpClientNames
{
    public const string Intervals = "Intervals";

    public const string GarminApi = "GarminApi";
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTrainerHttpClients(this IServiceCollection services)
    {
        // Unsafe methods never retry: a repeated upload could make a second activity.
        var retry = new HttpRetryStrategyOptions();
        retry.DisableForUnsafeHttpMethods();
        services.AddHttpClient(HttpClientNames.Intervals, IntervalsClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .AddResilienceHandler(HttpClientNames.Intervals, pipeline => pipeline.AddRetry(retry));

        services.AddHttpClient(HttpClientNames.GarminApi, GarminClient.ConfigureApi)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        return services;
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    };
}
