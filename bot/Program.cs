using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

public class Program
{
    private static DiscordSocketClient _client;
    private static InteractionService _interactions;
    private static IServiceProvider _services;
    private static ILogger _logger;

    public static async Task Main()
    {
        Console.WriteLine("🤖 Starting Discord Bot with Slash Commands...");
        
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ ERROR: DISCORD_TOKEN not found!");
            return;
        }

        // Настройка сервисов
        _services = ConfigureServices();
        _logger = _services.GetRequiredService<ILogger<Program>>();
        
        // Создание клиента
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.All,
            LogLevel = LogSeverity.Info
        });

        // Настройка InteractionService
        _interactions = new InteractionService(_client.Rest, new InteractionServiceConfig
        {
            LogLevel = LogSeverity.Info,
            DefaultRunMode = RunMode.Async
        });

        // Регистрация обработчиков
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.InteractionCreated += HandleInteractionAsync;

        // Регистрация модулей команд
        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        // Подключение к Discord
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        Console.WriteLine("✅ Bot started! Waiting for commands...");
        await Task.Delay(-1); // Бесконечное ожидание
    }

    private static IServiceProvider ConfigureServices()
    {
        return new ServiceCollection()
            .AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .BuildServiceProvider();
    }

    private static async Task ReadyAsync()
    {
        Console.WriteLine($"✅ Bot {_client.CurrentUser} is ready!");
        
        try
        {
            // Регистрация slash-команд
            await _interactions.RegisterCommandsGloballyAsync();
            Console.WriteLine("✅ Slash commands registered!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error registering commands: {ex.Message}");
        }

        await _client.SetActivityAsync(new Game("/help", ActivityType.Listening));
    }

    private static async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(context, _services);
            
            if (!result.IsSuccess)
            {
                Console.WriteLine($"❌ Command error: {result.ErrorReason}");
                
                if (interaction.Type == InteractionType.ApplicationCommand)
                {
                    await interaction.RespondAsync($"❌ Error: {result.ErrorReason}", ephemeral: true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Exception: {ex.Message}");
            
            if (interaction.Type == InteractionType.ApplicationCommand)
            {
                await interaction.RespondAsync($"❌ An error occurred: {ex.Message}", ephemeral: true);
            }
        }
    }

    private static Task LogAsync(LogMessage msg)
    {
        Console.WriteLine($"[{msg.Severity}] {msg.Message}");
        return Task.CompletedTask;
    }
}
