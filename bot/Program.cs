using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

public class Program
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public static async Task Main() => await new Program().RunAsync();

    public Program()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.All,
            LogLevel = LogSeverity.Info
        });

        _interactions = new InteractionService(_client.Rest, new InteractionServiceConfig
        {
            LogLevel = LogSeverity.Info,
            UseCompiledLambda = true
        });

        _services = ConfigureServices();
        _logger = _services.GetRequiredService<ILogger<Program>>();
    }

    private IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(_interactions)
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddSingleton<MusicService>()
            .AddSingleton<LoggingService>();

        return services.BuildServiceProvider();
    }

    private async Task RunAsync()
    {
        Console.Title = "Universal Discord Bot";
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("========================================");
        Console.WriteLine("       UNIVERSAL DISCORD BOT v2.0");
        Console.WriteLine("========================================");
        Console.WriteLine();

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ ERROR: DISCORD_TOKEN not found!");
            return;
        }

        await InitializeAsync();
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private async Task InitializeAsync()
    {
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.InteractionCreated += InteractionCreatedAsync;
        _client.UserJoined += UserJoinedAsync;

        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
    }

    private async Task ReadyAsync()
    {
        Console.WriteLine($"✅ Bot {_client.CurrentUser} ready!");
        Console.WriteLine($"🏰 Guilds: {_client.Guilds.Count}");

        // Регистрируем slash-команды глобально
        try
        {
            await _interactions.RegisterCommandsGloballyAsync();
            Console.WriteLine("✅ Slash commands registered globally!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error registering commands: {ex.Message}");
        }

        await _client.SetActivityAsync(new Game("/help for commands", ActivityType.Listening));
    }

    private async Task InteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(context, _services);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Command error: {ex.Message}");
            
            if (interaction.Type == InteractionType.ApplicationCommand)
            {
                await interaction.GetOriginalResponseAsync()
                    .ContinueWith(async msg => await msg.Result.DeleteAsync());
            }
        }
    }

    private async Task UserJoinedAsync(SocketGuildUser user)
    {
        // Автоматическая выдача роли
        var role = user.Guild.Roles.FirstOrDefault(r => 
            r.Name.Contains("Member", StringComparison.OrdinalIgnoreCase) && !r.IsEveryone);
        
        if (role != null)
        {
            try
            {
                await user.AddRoleAsync(role);
                Console.WriteLine($"✅ Role {role.Name} given to {user.Username}");
                
                var channel = user.Guild.SystemChannel;
                if (channel != null)
                    await channel.SendMessageAsync($"👋 Welcome {user.Mention}! You got {role.Mention}");
            }
            catch { }
        }
    }

    private Task LogAsync(LogMessage msg)
    {
        Console.WriteLine($"[{msg.Severity}] {msg.Message}");
        return Task.CompletedTask;
    }
}
