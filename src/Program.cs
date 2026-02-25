using TeamZaps.Configuration;
using TeamZaps.Handlers;
using TeamZaps.Services;
using TeamZaps.Backends;
using TeamZaps.Session;
using TeamZaps.Utils;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using TeamZaps.Logging;
using TeamZaps.Backends.Indexer;

namespace TeamZaps;

public static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var host = CreateHostBuilder(args).Build();
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "TeamZaps bot terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog((hostingContext, services, loggerConfiguration) =>
            {
                const string LogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
                loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(outputTemplate: LogTemplate)
                    .WriteTo.Map(
                        keySelector: _ => DateTime.Now.Year,
                        configure: (year, wt) => wt.File(
                            path: Path.Combine(Common.LogPath, year.ToString(), "log-.txt"),
                            rollingInterval: RollingInterval.Day,
                            outputTemplate: LogTemplate));
                            
                Trace.Listeners.Add(new DebugTraceListener());
            })
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                #if DEBUG
                // Default to development environment unless explicitly overridden:
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")))
                    hostingContext.HostingEnvironment.EnvironmentName = "Development";
                #endif

                Log.Information($"Starting {hostingContext.HostingEnvironment.ApplicationName} Telegram Bot in {hostingContext.HostingEnvironment.EnvironmentName.ToLower()} environment...");

                config
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Configure locale
                var botBehaviorConfig = hostContext.Configuration.GetSection(BotBehaviorOptions.SectionName);
                var locale = botBehaviorConfig.GetValue(nameof(BotBehaviorOptions.Locale), "en-US")!;
                try
                {
                    var cultureInfo = new CultureInfo(locale);
                    CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
                    CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
                    Log.Information("Locale set to {Locale}", locale);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to set locale to {Locale}, using default", locale);
                }

                services.Configure<BotBehaviorOptions>(botBehaviorConfig);
                services.Configure<TelegramSettings>(hostContext.Configuration.GetSection(TelegramSettings.SectionName));
                services.Configure<DebugSettings>(hostContext.Configuration.GetSection(DebugSettings.SectionName));
                services.Configure<RecoverySettings>(hostContext.Configuration.GetSection(RecoverySettings.SectionName));
                var backendsSection = hostContext.Configuration.GetSection("Backends");
                services.Configure<ElectrumXSettings>(backendsSection.GetSection(ElectrumXSettings.SectionName));
                services.Configure<LnbitsSettings>(backendsSection.GetSection(LnbitsSettings.SectionName));
                services.Configure<AlbyHubSettings>(backendsSection.GetSection(AlbyHubSettings.SectionName));

                // Register HttpClientFactory for backend services:
                services.AddHttpClient();

                services.AddSingleton(typeof(FileService<>));
                services.AddSingleton<RecoveryService>();
                services.AddHostedServiceAsSingleton<StatisticService>();
                services.AddSingleton<SessionManager>();
                services.AddSingleton<SessionWorkflowService>();
                services.AddHostedServiceAsSingleton<LiquidityLogService>();
                services.AddHostedServiceAsSingleton<PaymentMonitorService>();
                services.AddHostedService<RecoveryService>();
                services.AddHostedService<TelegramBotService>();
                services.AddSingleton<UpdateHandler>();
                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var settings = sp.GetRequiredService<IOptions<TelegramSettings>>().Value;
                    if (string.IsNullOrWhiteSpace(settings.BotToken))
                        throw new InvalidOperationException("Telegram bot token is not configured.");
                    return (new TelegramBotClient(settings.BotToken));
                });

                // Determine backends based on configuration:
                // > Select first configured backends.
                var configuredBackends = hostContext.Configuration
                    .GetSection("Backends")
                    .GetChildren()
                    .Select(c => c.Key)
                    .ToArray();

                // Inject backends:
                services.AddBackend<IIndexerBackend>(configuredBackends);
                services.AddBackend<IExchangeRateBackend>(configuredBackends);
                services.AddBackend<ILightningBackend>(configuredBackends);
            });


    #region Helper
    private static bool RequiresExchangeRateBackend(Type type) => type.GetConstructors()
        .SelectMany(c => c.GetParameters())
        .Any(p => (p.ParameterType == typeof(IExchangeRateBackend)));
    #endregion
}

internal static partial class Ext
{
    public static IServiceCollection AddHostedServiceAsSingleton<T>(this IServiceCollection source)
        where T : class, IHostedService
    {
        source.AddSingleton<T>();
        source.AddHostedService(sp => sp.GetRequiredService<T>());

        return (source);
    }
    public static IServiceCollection AddBackend<T>(this IServiceCollection source, string[] backends)
        where T : IBackend
    {
        var backendType = typeof(T).Name;
        var backend = TryGetBackendType<T>(backends);
        if (backend is null)
            throw new InvalidOperationException($"No backend of type '{backendType}' configured!");
        source.AddBackend<T>(backend);
        Log.Information($"Using '{backend.Name}' as backend of type '{backendType}'.");

        return (source);
    }
    public static IServiceCollection AddBackend<T>(this IServiceCollection source, Type backendType)
        where T : IBackend
    {
        // Register the concrete backend type as singleton:
        source.AddSingleton(backendType);

        // Register interface 'T' to resolve the same singleton instance:
        source.AddSingleton(typeof(T), sp => sp.GetRequiredService(backendType));

        // Register interface 'IBackend' to resolve the same singleton instance:
        if (typeof(T) != typeof(IBackend))
            source.AddSingleton(typeof(IBackend), sp => sp.GetRequiredService(backendType));

        // Register as hosted service if applicable:
        // > Must(!) use typeof(IHostedService) to properly register as hosted service
        if (backendType.IsAssignableTo<BackgroundService>())
            source.AddSingleton<IHostedService>(sp => (IHostedService)sp.GetRequiredService(backendType));

        return (source);
    }

    
    #region Helper
    private static Type? TryGetBackendType<T>(string[] backends)
        where T : IBackend
    {
        foreach (var backend in backends)
        {
            if (Common.BackendTypes.TryGetValue(backend.ToLowerInvariant(), out var backendType))
            {
                if (backendType.ProvidedInterfaces.Any(i => (i == typeof(T))))
                    return (backendType.Type);
            }
        }
        return (null);
    }
    #endregion
}

/// <summary>
/// TraceListener that forwards System.Diagnostics Debug/Trace output.
/// </summary>
internal sealed class DebugTraceListener : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            Log.Debug(message);
    }
    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            Log.Debug(message);
    }
}