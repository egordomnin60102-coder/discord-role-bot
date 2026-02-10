using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

Console.Title = "Discord Role Bot - GitHub Hosted";
Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("🤖 Discord Role Bot - Auto Role Assignment");
Console.WriteLine("===========================================");

// Получаем токен
var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.WriteLine("❌ ERROR: No DISCORD_TOKEN in GitHub Secrets!");
    return;
}

Console.WriteLine("✅ Token received");
Console.WriteLine("🚀 Starting bot...");

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | 
                   GatewayIntents.GuildMembers |
                   GatewayIntents.GuildMessages,
    LogLevel = LogSeverity.Info
});

// Логирование
client.Log += msg =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg.Message}");
    return Task.CompletedTask;
};

// Когда бот готов
client.Ready += () =>
{
    Console.WriteLine($"\n🎉 BOT READY: {client.CurrentUser}");
    Console.WriteLine($"🏰 Servers: {client.Guilds.Count}");
    
    // Показываем информацию о каждом сервере
    foreach (var guild in client.Guilds)
    {
        Console.WriteLine($"   • {guild.Name} (ID: {guild.Id})");
        Console.WriteLine($"     Members: {guild.MemberCount}, Roles: {guild.Roles.Count}");
    }
    
    Console.WriteLine("===========================================");
    return Task.CompletedTask;
};

// ⭐⭐⭐ ГЛАВНОЕ: ВЫДАЧА РОЛИ НОВЫМ УЧАСТНИКАМ ⭐⭐⭐
client.UserJoined += async user =>
{
    Console.WriteLine($"\n[🎉] NEW USER: {user.Username} joined {user.Guild.Name}");
    
    try
    {
        // 1. Ищем роль "Member" или "Участник" или "Новичок"
        var role = FindRoleForUser(user.Guild);
        
        if (role == null)
        {
            Console.WriteLine($"   ⚠️ No suitable role found on {user.Guild.Name}");
            Console.WriteLine($"   Available roles:");
            foreach (var r in user.Guild.Roles.Where(r => !r.IsEveryone).Take(5))
            {
                Console.WriteLine($"     • {r.Name} (ID: {r.Id})");
            }
            return;
        }
        
        Console.WriteLine($"   🎯 Found role: {role.Name} (ID: {role.Id})");
        
        // 2. Проверяем права бота
        var botUser = user.Guild.CurrentUser;
        if (botUser == null || !botUser.GuildPermissions.ManageRoles)
        {
            Console.WriteLine($"   ❌ Bot doesn't have 'Manage Roles' permission on {user.Guild.Name}");
            return;
        }
        
        // 3. Проверяем иерархию ролей
        var highestBotRole = botUser.Roles.OrderByDescending(r => r.Position).FirstOrDefault();
        if (highestBotRole == null || highestBotRole.Position <= role.Position)
        {
            Console.WriteLine($"   ❌ Bot role ({highestBotRole?.Name}) must be HIGHER than {role.Name}");
            Console.WriteLine($"      Bot role position: {highestBotRole?.Position}");
            Console.WriteLine($"      Target role position: {role.Position}");
            return;
        }
        
        // 4. Проверяем, есть ли уже эта роль
        if (user.Roles.Any(r => r.Id == role.Id))
        {
            Console.WriteLine($"   ℹ️ User already has role {role.Name}");
            return;
        }
        
        // 5. ВЫДАЁМ РОЛЬ! 🎉
        Console.WriteLine($"   ⚡ Assigning role {role.Name} to {user.Username}...");
        await user.AddRoleAsync(role);
        Console.WriteLine($"   ✅ SUCCESS: Role {role.Name} assigned to {user.Username}!");
        
        // 6. Отправляем приветствие
        await SendWelcomeMessage(user, role);
        
    }
    catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MissingPermissions)
    {
        Console.WriteLine($"   ❌ PERMISSION ERROR: {ex.Message}");
        Console.WriteLine($"   Fix: 1) Give bot 'Manage Roles' permission");
        Console.WriteLine($"        2) Make bot role higher than target role");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   💥 ERROR assigning role: {ex.Message}");
    }
};

// Функция поиска роли
SocketRole? FindRoleForUser(SocketGuild guild)
{
    // Список возможных названий ролей (по порядку приоритета)
    var possibleRoleNames = new[]
    {
        "Member", "Участник", "Members", "Участники",
        "Новичок", "New", "Новый", "Новые",
        "User", "Пользователь", "Гость", "Guest"
    };
    
    foreach (var roleName in possibleRoleNames)
    {
        var role = guild.Roles.FirstOrDefault(r => 
            r.Name.Contains(roleName, StringComparison.OrdinalIgnoreCase) && 
            !r.IsEveryone);
        
        if (role != null)
            return role;
    }
    
    // Если не нашли по имени, берём первую не-@everyone роль
    return guild.Roles.FirstOrDefault(r => !r.IsEveryone && r != guild.EveryoneRole);
}

// Функция отправки приветствия
async Task SendWelcomeMessage(SocketGuildUser user, SocketRole role)
{
    try
    {
        // Ищем куда отправить приветствие
        var channel = user.Guild.SystemChannel ?? 
                     user.Guild.TextChannels.FirstOrDefault(c => 
                         c.Name.Contains("общ") || 
                         c.Name.Contains("general") || 
                         c.Name.Contains("welcome"));
        
        if (channel != null)
        {
            var message = $"👋 Добро пожаловать, {user.Mention}! Ты получил роль {role.Mention}.";
            await channel.SendMessageAsync(message);
            Console.WriteLine($"   📨 Welcome sent to #{channel.Name}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ⚠️ Couldn't send welcome: {ex.Message}");
    }
}

// Команды для управления
client.MessageReceived += async message =>
{
    if (message.Author.IsBot || message is not SocketUserMessage userMessage)
        return;
    
    var content = userMessage.Content.ToLower();
    
    // Команда !setrole
    if (content.StartsWith("!setrole "))
    {
        var user = message.Author as SocketGuildUser;
        if (user == null || !user.GuildPermissions.ManageRoles)
        {
            await message.Channel.SendMessageAsync("❌ Нужны права **Manage Roles**!");
            return;
        }
        
        await message.Channel.SendMessageAsync(
            "⚙️ **Настройка роли:**\n" +
            "Бот автоматически ищет роли: Member, Участник, Новичок\n" +
            "Чтобы установить другую роль, нужно обновить код бота.\n" +
            "Бот работает на GitHub Actions - код можно изменить в репозитории!"
        );
    }
    // Команда !roleinfo
    else if (content == "!roleinfo")
    {
        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        if (guild == null) return;
        
        var role = FindRoleForUser(guild);
        
        if (role == null)
        {
            await message.Channel.SendMessageAsync("❌ Не найдена подходящая роль для выдачи.");
        }
        else
        {
            await message.Channel.SendMessageAsync(
                $"🎯 **Текущая роль для выдачи:** {role.Mention}\n" +
                $"📝 **Имя:** {role.Name}\n" +
                $"🆔 **ID:** {role.Id}\n" +
                $"🎨 **Цвет:** {role.Color}\n" +
                $"⬆️ **Позиция:** {role.Position}\n\n" +
                $"Новые участники будут получать эту роль автоматически!"
            );
        }
    }
    // Команда !ping
    else if (content == "!ping")
    {
        await message.Channel.SendMessageAsync(
            "🏓 **Pong!**\n" +
            "🤖 Бот для выдачи ролей\n" +
            "🏰 Серверов: " + client.Guilds.Count + "\n" +
            "🆓 Хостинг: GitHub Actions (бесплатно!)\n" +
            "⚡ Автовыдача ролей: ВКЛЮЧЕНО"
        );
    }
    // Команда !help
    else if (content == "!help")
    {
        var embed = new EmbedBuilder()
            .WithTitle("🤖 Role Bot - Помощь")
            .WithDescription("Автоматическая выдача ролей новым участникам")
            .WithColor(Color.Green)
            .AddField("🎯 Авто-функции", "• Выдача роли при входе\n• Приветственное сообщение", false)
            .AddField("🔧 Команды", 
                "`!ping` - Проверка работы\n" +
                "`!roleinfo` - Какая роль выдается\n" +
                "`!help` - Эта справка", false)
            .AddField("⚙️ Настройка", "Роль настраивается в коде бота", false)
            .WithFooter("Хостинг: GitHub Actions • Автоперезапуск каждые 6 часов")
            .Build();
            
        await message.Channel.SendMessageAsync(embed: embed);
    }
};

// Подключаемся
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

Console.WriteLine("\n✅ Bot started successfully!");
Console.WriteLine("🎯 Ready to assign roles to new members!");
Console.WriteLine("⏰ Will run for 5h45m, then auto-restart");

// Бесконечное ожидание
await Task.Delay(-1);
