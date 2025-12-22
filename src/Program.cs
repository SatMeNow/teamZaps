using teamZaps.Configuration;
using teamZaps.Handlers;
using teamZaps.Services;
using teamZaps.Backend;
using teamZaps.Sessions;
using teamZaps.Utils;
using Serilog;

namespace teamZaps;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Starting Team Zaps application");
            await CreateHostBuilder(args).Build().RunAsync().ConfigureAwait(false);
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
            .UseSerilog()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                #if DEBUG
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
                services.Configure<BotBehaviorOptions>(hostContext.Configuration.GetSection(BotBehaviorOptions.SectionName));
                services.Configure<TelegramSettings>(hostContext.Configuration.GetSection(TelegramSettings.SectionName));
                services.Configure<DebugSettings>(hostContext.Configuration.GetSection(DebugSettings.SectionName));
                var backendsSection = hostContext.Configuration.GetSection("Backends");
                services.Configure<LnbitsSettings>(backendsSection.GetSection(LnbitsSettings.SectionName));
                services.Configure<AlbyHubSettings>(backendsSection.GetSection(AlbyHubSettings.SectionName));

                // Register HttpClientFactory for backend services:
                services.AddHttpClient();

                services.AddHostedService<RecoveryService>();
                services.AddHostedService<TelegramBotService>();
                services.AddHostedService<PaymentMonitorService>();

                services.AddSingleton(typeof(FileService<>));
                services.AddSingleton<RecoveryService>();
                services.AddSingleton<SessionManager>();
                services.AddSingleton<SessionWorkflowService>();
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

                // Inject lightning backend:
                var lightningBackend = TryGetBackendType<ILightningBackend>(configuredBackends);
                if (lightningBackend is null)
                    throw new InvalidOperationException("No lightning backend configured!");
                services.AddSingleton(typeof(ILightningBackend), lightningBackend);
                Log.Information($"Using '{lightningBackend.Name}' as lightning backend");

                // Inject exchange rate backend if required:
                if (RequiresExchangeRateBackend(lightningBackend))
                {
                    Type? exchangeRateBackend;
                    if (lightningBackend.IsAssignableTo<IExchangeRateBackend>())
                        exchangeRateBackend = lightningBackend; // Use lightning backend also as exchange rate backend.
                    else
                        exchangeRateBackend = TryGetBackendType<IExchangeRateBackend>(configuredBackends);
                    if (exchangeRateBackend is null)
                        throw new InvalidOperationException("No exchange rate backend configured!");
                    services.AddSingleton(typeof(IExchangeRateBackend), exchangeRateBackend);
                    services.AddHostedService(f => (BackgroundService)f.GetRequiredService<IExchangeRateBackend>());
                    Log.Information($"Using '{exchangeRateBackend.Name}' as exchange rate backend");
                }
                else
                    Log.Information($"No exchange rate backend required.");
            });


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
    private static bool RequiresExchangeRateBackend(Type type) => type.GetConstructors()
        .SelectMany(c => c.GetParameters())
        .Any(p => (p.ParameterType == typeof(IExchangeRateBackend)));
    #endregion
}
