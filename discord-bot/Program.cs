using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class Program
{
    private static DiscordSocketClient _client;
    private static Dictionary<string, string> _config = new();
    
    public static async Task Main()
    {
        Console.WriteLine("🤖 Discord Role Bot - GitHub Hosted");
        Console.WriteLine("====================================");
        
        // Токен из Secrets GitHub
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ Токен не найден!");
            Console.WriteLine("Добавьте DISCORD_TOKEN в Secrets GitHub");
            return;
        }
        
        Console.WriteLine("✅ Токен получен");
        
        // Загружаем конфиг (будет в памяти, т.к. на GitHub нет постоянного хранилища)
        // Можно использовать GitHub Gist для хранения конфига
        
        _client = new DiscordSocketClient();
        
        _client.Log += msg =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg.Message}");
            return Task.CompletedTask;
        };
        
        _client.Ready += () =>
        {
            Console.WriteLine($"✅ Бот {_client.CurrentUser} готов!");
            Console.WriteLine($"🏰 Серверов: {_client.Guilds.Count}");
            return Task.CompletedTask;
        };
        
        _client.UserJoined += async user =>
        {
            Console.WriteLine($"🎉 Новый: {user.Username}");
            
            // Ищем роль "Member" или "Участник"
            var role = user.Guild.Roles.FirstOrDefault(r => 
                (r.Name.Contains("Member", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.Contains("Участник", StringComparison.OrdinalIgnoreCase)) &&
                !r.IsEveryone);
            
            if (role != null)
            {
                try
                {
                    await user.AddRoleAsync(role);
                    Console.WriteLine($"✅ Роль выдана: {role.Name}");
                    
                    var channel = user.Guild.SystemChannel;
                    if (channel != null)
                        await channel.SendMessageAsync($"👋 Добро пожаловать, {user.Mention}!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ошибка: {ex.Message}");
                }
            }
        };
        
        _client.MessageReceived += async message =>
        {
            if (message.Author.IsBot) return;
            
            if (message.Content.StartsWith("!setrole"))
            {
                var user = message.Author as SocketGuildUser;
                if (user == null || !user.GuildPermissions.ManageRoles)
                {
                    await message.Channel.SendMessageAsync("❌ Нужны права Manage Roles!");
                    return;
                }
                
                await message.Channel.SendMessageAsync("✅ Роль установлена! (бот на GitHub)");
            }
            else if (message.Content == "!ping")
            {
                await message.Channel.SendMessageAsync("🏓 Pong! Bot hosted on GitHub Actions");
            }
        };
        
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        
        // GitHub Actions будет убивать процесс через 6 часов
        // Поэтому просто ждем
        await Task.Delay(Timeout.Infinite);
    }
}