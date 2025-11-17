using teamZaps.Configuration;
using teamZaps.Handlers;
using teamZaps.Services;
using teamZaps.Sessions;

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
            await CreateHostBuilder(args).Build().RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
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

                config
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.Configure<TelegramSettings>(hostContext.Configuration.GetSection(TelegramSettings.SectionName));
                services.Configure<LnbitsSettings>(hostContext.Configuration.GetSection(LnbitsSettings.SectionName));
                services.Configure<BotBehaviorOptions>(hostContext.Configuration.GetSection(nameof(BotBehaviorOptions)));

                services.AddSingleton<LnbitsService>();
                services.AddSingleton<SessionManager>();
                services.AddSingleton<SessionWorkflowService>();

                services.AddSingleton<UpdateHandler>();

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    var settings = sp.GetRequiredService<IOptions<TelegramSettings>>().Value;
                    if (string.IsNullOrWhiteSpace(settings.BotToken))
                        throw new InvalidOperationException("Telegram bot token is not configured.");
                    return new TelegramBotClient(settings.BotToken);
                });

                services.AddHostedService<TelegramBotService>();
                services.AddHostedService<PaymentMonitorService>();
            });
}
