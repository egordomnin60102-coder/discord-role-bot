using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

Console.Title = "Discord Moderation Bot - GitHub Hosted";
Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("🤖 Discord Moderation Bot - Auto Role + Moderation");
Console.WriteLine("==================================================");

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
                   GatewayIntents.GuildMessages |
                   GatewayIntents.GuildVoiceStates,
    LogLevel = LogSeverity.Info
});

// Словарь для хранения предупреждений пользователей
var userWarnings = new Dictionary<ulong, Dictionary<ulong, List<Warning>>>();
// Словарь для таймеров мутов/банов
var activeTimers = new Dictionary<string, Timer>();

// Класс для предупреждений
public class Warning
{
    public string Reason { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public ulong ModeratorId { get; set; }
}

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
    
    foreach (var guild in client.Guilds)
    {
        Console.WriteLine($"   • {guild.Name} (ID: {guild.Id})");
        Console.WriteLine($"     Members: {guild.MemberCount}, Roles: {guild.Roles.Count}");
        
        // Инициализируем словарь предупреждений для сервера
        if (!userWarnings.ContainsKey(guild.Id))
        {
            userWarnings[guild.Id] = new Dictionary<ulong, List<Warning>>();
        }
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
        
        // 7. Логируем в лог-канал
        await LogToModChannel(user.Guild, 
            $"🎉 **Новый участник**\n" +
            $"👤 Пользователь: {user.Mention} (`{user.Username}`)\n" +
            $"🎭 Получена роль: {role.Mention}\n" +
            $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
        
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

// События для логирования
client.UserBanned += async (user, guild) =>
{
    await LogToModChannel(guild, 
        $"🔨 **Пользователь забанен**\n" +
        $"👤 Пользователь: `{user.Username}`\n" +
        $"🆔 ID: `{user.Id}`\n" +
        $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
};

client.UserUnbanned += async (user, guild) =>
{
    await LogToModChannel(guild, 
        $"🔓 **Пользователь разбанен**\n" +
        $"👤 Пользователь: `{user.Username}`\n" +
        $"🆔 ID: `{user.Id}`\n" +
        $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
};

client.UserLeft += async (guild, user) =>
{
    await LogToModChannel(guild, 
        $"🚪 **Пользователь покинул сервер**\n" +
        $"👤 Пользователь: `{user.Username}`\n" +
        $"🆔 ID: `{user.Id}`\n" +
        $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
};

client.UserVoiceStateUpdated += async (user, oldState, newState) =>
{
    if (user is SocketGuildUser guildUser)
    {
        var guild = guildUser.Guild;
        if (oldState.VoiceChannel == null && newState.VoiceChannel != null)
        {
            await LogToModChannel(guild, 
                $"🎤 **Пользователь зашел в голосовой канал**\n" +
                $"👤 Пользователь: {guildUser.Mention}\n" +
                $"📢 Канал: {newState.VoiceChannel.Name}\n" +
                $"📅 Время: {DateTime.Now:HH:mm}");
        }
        else if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
        {
            await LogToModChannel(guild, 
                $"🔇 **Пользователь вышел из голосового канала**\n" +
                $"👤 Пользователь: {guildUser.Mention}\n" +
                $"📢 Канал: {oldState.VoiceChannel.Name}\n" +
                $"📅 Время: {DateTime.Now:HH:mm}");
        }
        else if (oldState.VoiceChannel != null && newState.VoiceChannel != null && oldState.VoiceChannel.Id != newState.VoiceChannel.Id)
        {
            await LogToModChannel(guild, 
                $"🔄 **Пользователь перешел в другой голосовой канал**\n" +
                $"👤 Пользователь: {guildUser.Mention}\n" +
                $"📢 С: {oldState.VoiceChannel.Name}\n" +
                $"📢 На: {newState.VoiceChannel.Name}\n" +
                $"📅 Время: {DateTime.Now:HH:mm}");
        }
    }
};

client.RoleCreated += async role =>
{
    await LogToModChannel(role.Guild, 
        $"🆕 **Создана новая роль**\n" +
        $"🎭 Роль: {role.Mention}\n" +
        $"🎨 Цвет: {role.Color}\n" +
        $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
};

client.RoleDeleted += async (role, guild) =>
{
    await LogToModChannel(guild,

        $"🗑️ **Роль удалена**\n" +
        $"🎭 Роль: `{role.Name}`\n" +
        $"🆔 ID: `{role.Id}`\n" +
        $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
};

client.UserUpdated += async (oldUser, newUser) =>
{
    if (oldUser is SocketGuildUser oldGuildUser && newUser is SocketGuildUser newGuildUser)
    {
        var guild = newGuildUser.Guild;
        
        // Проверяем изменения в ролях
        var oldRoles = oldGuildUser.Roles.Select(r => r.Id).ToHashSet();
        var newRoles = newGuildUser.Roles.Select(r => r.Id).ToHashSet();
        
        if (!oldRoles.SetEquals(newRoles))
        {
            var addedRoles = newRoles.Except(oldRoles).Select(id => guild.GetRole(id)).Where(r => r != null);
            var removedRoles = oldRoles.Except(newRoles).Select(id => guild.GetRole(id)).Where(r => r != null);
            
            foreach (var role in addedRoles)
            {
                await LogToModChannel(guild, 
                    $"➕ **Пользователю добавлена роль**\n" +
                    $"👤 Пользователь: {newGuildUser.Mention}\n" +
                    $"🎭 Роль: {role.Mention}\n" +
                    $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
            }
            
            foreach (var role in removedRoles)
            {
                await LogToModChannel(guild, 
                    $"➖ **У пользователя удалена роль**\n" +
                    $"👤 Пользователь: {newGuildUser.Mention}\n" +
                    $"🎭 Роль: {role.Mention}\n" +
                    $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
            }
        }
    }
};

// Команды для управления
client.MessageReceived += async message =>
{
    if (message.Author.IsBot || message is not SocketUserMessage userMessage)
        return;
    
    var guild = (message.Channel as SocketGuildChannel)?.Guild;
    if (guild == null) return;
    
    var content = userMessage.Content;
    var lowerContent = content.ToLower();
    
    // Обработка слэш-команд и обычных команд
    if (content.StartsWith("/") || content.StartsWith("!"))
    {
        var args = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = args[0].ToLower().TrimStart('/', '!');
        
        switch (command)
        {
            case "tempmute":
                await HandleTempMute(userMessage, args, guild);
                break;
                
            case "tempban":
                await HandleTempBan(userMessage, args, guild);
                break;
                
            case "clear":
            case "purge":
                await HandleClearMessages(userMessage, args, guild);
                break;
                
            case "warn":
                await HandleWarn(userMessage, args, guild);
                break;
                
            case "warnings":
                await HandleShowWarnings(userMessage, args, guild);
                break;
                
            case "removewarn":
                await HandleRemoveWarning(userMessage, args, guild);
                break;
                
            case "kick":
                await HandleKick(userMessage, args, guild);
                break;
                
            case "ban":
                await HandleBan(userMessage, args, guild);
                break;
                
            case "mute":
                await HandleMute(userMessage, args, guild);
                break;
                
            case "unmute":
                await HandleUnmute(userMessage, args, guild);
                break;
                
            case "unban":
                await HandleUnban(userMessage, args, guild);
                break;
                
            case "setrole":
                await HandleSetRole(userMessage, args, guild);
                break;
                
            case "roleinfo":
                await HandleRoleInfo(userMessage, guild);
                break;
                
            case "ping":
                await HandlePing(userMessage, guild);
                break;
                
            case "help":
                await HandleHelp(userMessage, guild);
                break;
                
            case "modlog":
                await HandleModLog(userMessage, args, guild);
                break;
                
            case "modstats":
                await HandleModStats(userMessage, guild);
                break;
        }
    }
};

// === ОБРАБОТЧИКИ КОМАНД ===

async Task HandleTempMute(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.MuteMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Mute Members**!");
        return;
    }
    
    if (args.Length < 3)
    {
        await message.Channel.SendMessageAsync(
            "❌ Использование: `/tempmute @пользователь время причина`\n" +
            "Пример: `/tempmute @Спамер 30m Реклама`\n" +
            "Время: 30s, 5m, 2h, 1d");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя замутить себя!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя замутить пользователя с равной или более высокой ролью!");
        return;
    }
    
    var timeString = args[2];
    var reason = args.Length > 3 ? string.Join(" ", args.Skip(3)) : "Не указана";
    
    if (!TryParseTime(timeString, out var timeSpan))
    {
        await message.Channel.SendMessageAsync("❌ Неверный формат времени! Используйте: 30s, 5m, 2h, 1d");
        return;
    }
    
    var muteRole = await GetOrCreateMuteRole(guild);
    if (muteRole == null)
    {
        await message.Channel.SendMessageAsync("❌ Не удалось создать/найти роль для мута!");
        return;
    }
    
    await user.AddRoleAsync(muteRole);
    
    var timer = new Timer(async _ =>
    {
        try
        {
            if (user.Roles.Any(r => r.Id == muteRole.Id))
            {
                await user.RemoveRoleAsync(muteRole);
                await LogToModChannel(guild,
                    $"🔓 **Автоматический размут**\n" +
                    $"👤 Пользователь: {user.Mention}\n" +
                    $"⏰ Был замучен на: {timeString}\n" +
                    $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
            }
        }
        catch { }
    }, null, timeSpan, Timeout.InfiniteTimeSpan);
    
    var timerKey = $"mute_{guild.Id}_{user.Id}";
    if (activeTimers.ContainsKey(timerKey))
    {
        activeTimers[timerKey].Dispose();
    }
    activeTimers[timerKey] = timer;
    
    await message.Channel.SendMessageAsync(
        $"🔇 Пользователь {user.Mention} замучен на **{timeString}**\n" +
        $"📝 Причина: {reason}\n" +
        $"⏰ Размут в: {DateTime.Now.Add(timeSpan):HH:mm}");
    
    await LogToModChannel(guild,
        $"🔇 **Временный мут**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"⏰ Время: {timeString}\n" +
        $"📝 Причина: {reason}");
}

async Task HandleTempBan(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.BanMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Ban Members**!");
        return;
    }
    
    if (args.Length < 3)
    {
        await message.Channel.SendMessageAsync(
            "❌ Использование: `/tempban @пользователь время причина`\n" +
            "Пример: `/tempban @Нарушитель 7d Оскорбления`\n" +
            "Время: 30s, 5m, 2h, 1d, 7d");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя забанить себя!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя забанить пользователя с равной или более высокой ролью!");
        return;
    }
    
    var timeString = args[2];
    var reason = args.Length > 3 ? string.Join(" ", args.Skip(3)) : "Не указана";
    
    if (!TryParseTime(timeString, out var timeSpan))
    {
        await message.Channel.SendMessageAsync("❌ Неверный формат времени! Используйте: 30s, 5m, 2h, 1d, 7d");
        return;
    }
    
    await guild.AddBanAsync(user, 0, reason);
    
    var timer = new Timer(async _ =>
    {
        try
        {
            await guild.RemoveBanAsync(user);
            await LogToModChannel(guild,
                $"🔓 **Автоматический разбан**\n" +
                $"👤 Пользователь: `{user.Username}`\n" +
                $"⏰ Был забанен на: {timeString}\n" +
                $"📅 Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
        }
        catch { }
    }, null, timeSpan, Timeout.InfiniteTimeSpan);
    
    var timerKey = $"ban_{guild.Id}_{user.Id}";
    if (activeTimers.ContainsKey(timerKey))
    {
        activeTimers[timerKey].Dispose();
    }
    activeTimers[timerKey] = timer;
    
    await message.Channel.SendMessageAsync(
        $"🔨 Пользователь {user.Mention} забанен на **{timeString}**\n" +
        $"📝 Причина: {reason}\n" +
        $"⏰ Разбан в: {DateTime.Now.Add(timeSpan):dd.MM.yyyy HH:mm}");
    
    await LogToModChannel(guild,
        $"🔨 **Временный бан**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"⏰ Время: {timeString}\n" +
        $"📝 Причина: {reason}");
}

async Task HandleClearMessages(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.ManageMessages)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Manage Messages**!");
        return;
    }
    
    if (args.Length < 2 || !int.TryParse(args[1], out var count) || count < 1 || count > 100)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/clear количество` (1-100)");
        return;
    }
    
    var messages = await message.Channel.GetMessagesAsync(count + 1).FlattenAsync();
    var filteredMessages = messages.Where(m => (DateTime.UtcNow - m.CreatedAt).TotalDays <= 14);
    
    if (message.Channel is SocketTextChannel textChannel)
    {
        await textChannel.DeleteMessagesAsync(filteredMessages);
        var reply = await message.Channel.SendMessageAsync($"🧹 Удалено {filteredMessages.Count() - 1} сообщений!");
        await Task.Delay(3000);
        await reply.DeleteAsync();
        
        await LogToModChannel(guild,
            $"🧹 **Очистка сообщений**\n" +
            $"👮 Модератор: {author.Mention}\n" +
            $"📊 Удалено: {filteredMessages.Count() - 1} сообщений\n" +
            $"📅 Время: {DateTime.Now:HH:mm}");
    }
}

async Task HandleWarn(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.KickMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Kick Members**!");
        return;
    }
    
    if (args.Length < 3)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/warn @пользователь причина`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя выдать предупреждение себе!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя выдать предупреждение пользователю с равной или более высокой ролью!");
        return;
    }
    
    var reason = string.Join(" ", args.Skip(2));
    
    // Добавляем предупреждение
    if (!userWarnings.ContainsKey(guild.Id))
        userWarnings[guild.Id] = new Dictionary<ulong, List<Warning>>();
    
    if (!userWarnings[guild.Id].ContainsKey(user.Id))
        userWarnings[guild.Id][user.Id] = new List<Warning>();
    
    userWarnings[guild.Id][user.Id].Add(new Warning
    {
        Reason = reason,
        Date = DateTime.Now,
        ModeratorId = author.Id
    });
    
    var warningCount = userWarnings[guild.Id][user.Id].Count;
    
    // Автоматические действия при накоплении предупреждений
    string autoAction = "";
    if (warningCount >= 5)
    {
        await guild.AddBanAsync(user, 0, "5 предупреждений");
        autoAction = "🔨 Автоматический бан (5 предупреждений)";
    }
    else if (warningCount >= 3)
    {
        var muteRole = await GetOrCreateMuteRole(guild);
        if (muteRole != null)
        {
            await user.AddRoleAsync(muteRole);
            autoAction = "🔇 Автоматический мут на 1 час (3 предупреждения)";
            
            // Авто-размут через 1 час
            var timer = new Timer(async _ =>
            {
                try
                {
                    if (user.Roles.Any(r => r.Id == muteRole.Id))
                    {
                        await user.RemoveRoleAsync(muteRole);
                    }
                }
                catch { }
            }, null, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
            
            var timerKey = $"auto_mute_{guild.Id}_{user.Id}";
            if (activeTimers.ContainsKey(timerKey))
                activeTimers[timerKey].Dispose();
            activeTimers[timerKey] = timer;
        }
    }
    
    await message.Channel.SendMessageAsync(
        $"⚠️ Пользователю {user.Mention} выдано предупреждение\n" +
        $"📝 Причина: {reason}\n" +
        $"📊 Всего предупреждений: {warningCount}\n" +
        (string.IsNullOrEmpty(autoAction) ? "" : $"⚡ {autoAction}"));
    
    await LogToModChannel(guild,
        $"⚠️ **Выдано предупреждение**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"📝 Причина: {reason}\n" +
        $"📊 Всего: {warningCount}\n" +
        (string.IsNullOrEmpty(autoAction) ? "" : $"⚡ {autoAction}"));
}

async Task HandleShowWarnings(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.KickMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Kick Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/warnings @пользователь`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (!userWarnings.ContainsKey(guild.Id) || !userWarnings[guild.Id].ContainsKey(user.Id) || userWarnings[guild.Id][user.Id].Count == 0)
    {
        await message.Channel.SendMessageAsync($"✅ У пользователя {user.Mention} нет предупреждений.");
        return;
    }
    
    var warnings = userWarnings[guild.Id][user.Id];
    var embed = new EmbedBuilder()
        .WithTitle($"⚠️ Предупреждения пользователя {user.Username}")
        .WithColor(Color.Orange)
        .WithThumbnailUrl(user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl());
    
    for (int i = 0; i < warnings.Count; i++)
    {
        var warning = warnings[i];
        var moderator = guild.GetUser(warning.ModeratorId);
        embed.AddField($"Предупреждение #{i + 1}",
            $"📝 **Причина:** {warning.Reason}\n" +
            $"👮 **Модератор:** {(moderator?.Mention ?? $"ID: {warning.ModeratorId}")}\n" +
            $"📅 **Дата:** {warning.Date:dd.MM.yyyy HH:mm}", false);
    }
    
    embed.WithFooter($"Всего предупреждений: {warnings.Count}");
    
    await message.Channel.SendMessageAsync(embed: embed.Build());
}

async Task HandleRemoveWarning(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.KickMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Kick Members**!");
        return;
    }
    
    if (args.Length < 3)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/removewarn @пользователь номер`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (!int.TryParse(args[2], out var warnNumber) || warnNumber < 1)
    {
        await message.Channel.SendMessageAsync("❌ Неверный номер предупреждения!");
        return;
    }
    
    if (!userWarnings.ContainsKey(guild.Id) || !userWarnings[guild.Id].ContainsKey(user.Id) || warnNumber > userWarnings[guild.Id][user.Id].Count)
    {
        await message.Channel.SendMessageAsync("❌ Предупреждение не найдено!");
        return;
    }
    
    userWarnings[guild.Id][user.Id].RemoveAt(warnNumber - 1);
    
    if (userWarnings[guild.Id][user.Id].Count == 0)
    {
        userWarnings[guild.Id].Remove(user.Id);
    }
    
    await message.Channel.SendMessageAsync($"✅ Предупреждение #{warnNumber} удалено у пользователя {user.Mention}");
    
    await LogToModChannel(guild,
        $"✅ **Удалено предупреждение**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"🔢 Номер: {warnNumber}");
}

async Task HandleKick(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.KickMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Kick Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/kick @пользователь [причина]`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя кикнуть себя!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя кикнуть пользователя с равной или более высокой ролью!");
        return;
    }
    
    var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "Не указана";
    
    await user.KickAsync(reason);
    
    await message.Channel.SendMessageAsync(
        $"👢 Пользователь {user.Mention} был кикнут\n" +
        $"📝 Причина: {reason}");
    
    await LogToModChannel(guild,
        $"👢 **Пользователь кикнут**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"📝 Причина: {reason}");
}

async Task HandleBan(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.BanMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Ban Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/ban @пользователь [причина]`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя забанить себя!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя забанить пользователя с равной или более высокой ролью!");
        return;
    }
    
    var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "Не указана";
    
    await guild.AddBanAsync(user, 0, reason);
    
    await message.Channel.SendMessageAsync(
        $"🔨 Пользователь {user.Mention} забанен\n" +
        $"📝 Причина: {reason}");
}

async Task HandleMute(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.MuteMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Mute Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/mute @пользователь [причина]`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    if (user.Id == author.Id)
    {
        await message.Channel.SendMessageAsync("❌ Нельзя замутить себя!");
        return;
    }
    
    if (!(author.GuildPermissions.Administrator || author.Hierarchy > user.Hierarchy))
    {
        await message.Channel.SendMessageAsync("❌ Нельзя замутить пользователя с равной или более высокой ролью!");
        return;
    }
    
    var reason = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "Не указана";
    var muteRole = await GetOrCreateMuteRole(guild);
    
    if (muteRole == null)
    {
        await message.Channel.SendMessageAsync("❌ Не удалось создать/найти роль для мута!");
        return;
    }
    
    await user.AddRoleAsync(muteRole);
    
    await message.Channel.SendMessageAsync(
        $"🔇 Пользователь {user.Mention} замучен\n" +
        $"📝 Причина: {reason}");
    
    await LogToModChannel(guild,
        $"🔇 **Пользователь замучен**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}\n" +
        $"📝 Причина: {reason}");
}

async Task HandleUnmute(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.MuteMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Mute Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/unmute @пользователь`");
        return;
    }
    
    var user = await GetUserFromMention(args[1], guild);
    if (user == null)
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден!");
        return;
    }
    
    var muteRole = await GetOrCreateMuteRole(guild);
    if (muteRole == null)
    {
        await message.Channel.SendMessageAsync("❌ Роль для мута не найдена!");
        return;
    }
    
    if (!user.Roles.Any(r => r.Id == muteRole.Id))
    {
        await message.Channel.SendMessageAsync($"ℹ️ Пользователь {user.Mention} не замучен.");
        return;
    }
    
    await user.RemoveRoleAsync(muteRole);
    
    // Удаляем таймер если есть
    var timerKey = $"mute_{guild.Id}_{user.Id}";
    if (activeTimers.ContainsKey(timerKey))
    {
        activeTimers[timerKey].Dispose();
        activeTimers.Remove(timerKey);
    }
    
    await message.Channel.SendMessageAsync($"🔓 Пользователь {user.Mention} размучен");
    
    await LogToModChannel(guild,
        $"🔓 **Пользователь размучен**\n" +
        $"👤 Пользователь: {user.Mention}\n" +
        $"👮 Модератор: {author.Mention}");
}

async Task HandleUnban(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.BanMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Ban Members**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync("❌ Использование: `/unban ID_пользователя`");
        return;
    }
    
    if (!ulong.TryParse(args[1], out var userId))
    {
        await message.Channel.SendMessageAsync("❌ Неверный ID пользователя!");
        return;
    }
    
    try
    {
        await guild.RemoveBanAsync(userId);
        await message.Channel.SendMessageAsync($"🔓 Пользователь с ID `{userId}` разбанен");
        
        // Удаляем таймер если есть
        var timerKey = $"ban_{guild.Id}_{userId}";
        if (activeTimers.ContainsKey(timerKey))
        {
            activeTimers[timerKey].Dispose();
            activeTimers.Remove(timerKey);
        }
        
        await LogToModChannel(guild,
            $"🔓 **Пользователь разбанен**\n" +
            $"👤 ID пользователя: `{userId}`\n" +
            $"👮 Модератор: {author.Mention}");
    }
    catch
    {
        await message.Channel.SendMessageAsync("❌ Пользователь не найден в списке банов!");
    }
}

async Task HandleSetRole(SocketUserMessage message, string[] args, SocketGuild guild)
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
        "Бот работает на GitHub Actions - код можно изменить в репозитории!");
}

async Task HandleRoleInfo(SocketUserMessage message, SocketGuild guild)
{
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
            $"Новые участники будут получать эту роль автоматически!");
    }
}

async Task HandlePing(SocketUserMessage message, SocketGuild guild)
{
    await message.Channel.SendMessageAsync(
        "🏓 **Pong!**\n" +
        "🤖 Модерационный бот с автовыдачей ролей\n" +
        "🏰 Серверов: " + client.Guilds.Count + "\n" +
        "🆓 Хостинг: GitHub Actions\n" +
        "⚡ Автовыдача ролей: ВКЛЮЧЕНО\n" +
        "🛡️ Система модерации: АКТИВНА");
}

async Task HandleHelp(SocketUserMessage message, SocketGuild guild)
{
    var embed = new EmbedBuilder()
        .WithTitle("🤖 Moderation Bot - Помощь")
        .WithDescription("Автоматическая выдача ролей + система модерации")
        .WithColor(Color.Blue)
        .AddField("🎯 Авто-функции", 
            "• Выдача роли при входе\n• Приветственное сообщение\n• Логирование действий", false)
        .AddField("🛡️ Модерационные команды", 
            "`/tempmute @user время причина` - Временный мут\n" +
            "`/tempban @user время причина` - Временный бан\n" +
            "`/clear количество` - Удалить сообщения\n" +
            "`/warn @user причина` - Выдать предупреждение\n" +
            "`/warnings @user` - Показать предупреждения\n" +
            "`/removewarn @user номер` - Удалить предупреждение\n" +
            "`/kick @user причина` - Кикнуть пользователя\n" +
            "`/ban @user причина` - Забанить пользователя\n" +
            "`/mute @user причина` - Замутить\n" +
            "`/unmute @user` - Размутить\n" +
            "`/unban ID` - Разбанить", false)
        .AddField("🔧 Основные команды", 
            "`/ping` - Проверка работы\n" +
            "`/roleinfo` - Какая роль выдается\n" +
            "`/help` - Эта справка\n" +
            "`/modlog` - Настроить лог-канал\n" +
            "`/modstats` - Статистика модерации", false)
        .AddField("⚙️ Настройка", 
            "• Роль настраивается в коде бота\n" +
            "• Лог-канал: `/modlog #канал`\n" +
            "• Автодействия при 3+ варнах", false)
        .WithFooter("Хостинг: GitHub Actions • Автоперезапуск каждые 6 часов")
        .Build();
        
    await message.Channel.SendMessageAsync(embed: embed);
}

async Task HandleModLog(SocketUserMessage message, string[] args, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.Administrator)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Administrator**!");
        return;
    }
    
    if (args.Length < 2)
    {
        await message.Channel.SendMessageAsync(
            "❌ Использование: `/modlog #канал`\n" +
            "Чтобы отключить логирование: `/modlog off`");
        return;
    }
    
    if (args[1].ToLower() == "off")
    {
        // Здесь можно реализовать сохранение настроек
        await message.Channel.SendMessageAsync("✅ Логирование отключено (функциональность сохранения настроек требует базы данных)");
        return;
    }
    
    await message.Channel.SendMessageAsync(
        "✅ В текущей версии бот ищет каналы с названиями:\n" +
        "• `mod-log`\n• `logs`\n• `moderator`\n• `модерация`\n• `логи`\n\n" +
        "Для сохранения настроек канала нужна база данных.");
}

async Task HandleModStats(SocketUserMessage message, SocketGuild guild)
{
    var author = message.Author as SocketGuildUser;
    if (author == null || !author.GuildPermissions.KickMembers)
    {
        await message.Channel.SendMessageAsync("❌ Нужны права **Kick Members**!");
        return;
    }
    
    if (!userWarnings.ContainsKey(guild.Id) || userWarnings[guild.Id].Count == 0)
    {
        await message.Channel.SendMessageAsync("📊 На этом сервере еще нет предупреждений.");
        return;
    }
    
    var totalWarnings = userWarnings[guild.Id].Sum(x => x.Value.Count);
    var topUsers = userWarnings[guild.Id]
        .OrderByDescending(x => x.Value.Count)
        .Take(5)
        .Select(x => {
            var user = guild.GetUser(x.Key);
            return $"• {(user?.Mention ?? $"ID: {x.Key}")}: {x.Value.Count} предупреждений";
        });
    
    var embed = new EmbedBuilder()
        .WithTitle("📊 Статистика модерации")
        .WithColor(Color.Purple)
        .AddField("Всего предупреждений", totalWarnings.ToString(), true)
        .AddField("Пользователей с предупреждениями", userWarnings[guild.Id].Count.ToString(), true)
        .AddField("Топ нарушителей", string.Join("\n", topUsers), false)
        .WithFooter($"Сервер: {guild.Name}")
        .Build();
    
    await message.Channel.SendMessageAsync(embed: embed.Build());
}

// === ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ ===

SocketRole? FindRoleForUser(SocketGuild guild)
{
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
    
    return guild.Roles.FirstOrDefault(r => !r.IsEveryone && r != guild.EveryoneRole);
}

async Task SendWelcomeMessage(SocketGuildUser user, SocketRole role)
{
    try
    {
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

async Task<SocketGuildUser?> GetUserFromMention(string mention, SocketGuild guild)
{
    if (MentionUtils.TryParseUser(mention, out var userId))
    {
        return guild.GetUser(userId);
    }
    
    // Попробуем найти по имени
    var users = await guild.GetUsersAsync().FlattenAsync();
    return users.FirstOrDefault(u => 
        u.Username.Contains(mention.Trim('@'), StringComparison.OrdinalIgnoreCase) ||
        (u.Nickname != null && u.Nickname.Contains(mention.Trim('@'), StringComparison.OrdinalIgnoreCase)));
}

bool TryParseTime(string input, out TimeSpan timeSpan)
{
    timeSpan = TimeSpan.Zero;
    
    var match = Regex.Match(input, @"^(\d+)([smhd])$", RegexOptions.IgnoreCase);
    if (!match.Success) return false;
    
    if (!int.TryParse(match.Groups[1].Value, out var value)) return false;
    
    return match.Groups[2].Value.ToLower() switch
    {
        "s" => (timeSpan = TimeSpan.FromSeconds(value)) != TimeSpan.Zero,
        "m" => (timeSpan = TimeSpan.FromMinutes(value)) != TimeSpan.Zero,
        "h" => (timeSpan = TimeSpan.FromHours(value)) != TimeSpan.Zero,
        "d" => (timeSpan = TimeSpan.FromDays(value)) != TimeSpan.Zero,
        _ => false
    };
}

async Task<SocketRole?> GetOrCreateMuteRole(SocketGuild guild)
{
    // Ищем существующую роль
    var muteRole = guild.Roles.FirstOrDefault(r => 
        r.Name.Equals("Muted", StringComparison.OrdinalIgnoreCase) ||
        r.Name.Equals("Мут", StringComparison.OrdinalIgnoreCase) ||
        r.Name.Equals("Заглушен", StringComparison.OrdinalIgnoreCase));
    
    if (muteRole != null) return muteRole;
    
    // Создаем новую роль
    try
    {
        var botUser = guild.CurrentUser;
        if (botUser == null || !botUser.GuildPermissions.ManageRoles) return null;
        
        muteRole = await guild.CreateRoleAsync("Muted", GuildPermissions.None, Color.DarkGrey, false, false);
        
        // Отключаем права для всех каналов
        foreach (var channel in guild.TextChannels)
        {
            try
            {
                await channel.AddPermissionOverwriteAsync(muteRole, 
                    new OverwritePermissions(
                        sendMessages: PermValue.Deny,
                        addReactions: PermValue.Deny,
                        speak: PermValue.Deny));
            }
            catch { }
        }
        
        foreach (var channel in guild.VoiceChannels)
        {
            try
            {
                await channel.AddPermissionOverwriteAsync(muteRole, 
                    new OverwritePermissions(connect: PermValue.Deny, speak: PermValue.Deny));
            }
            catch { }
        }
        
        return muteRole;
    }
    catch
    {
        return null;
    }
}

async Task LogToModChannel(SocketGuild guild, string message)
{
    try
    {
        // Ищем лог-канал
        var logChannel = guild.TextChannels.FirstOrDefault(c => 
            c.Name.Contains("mod-log") ||
            c.Name.Contains("logs") ||
            c.Name.Contains("moderator") ||
            c.Name.Contains("модерация") ||
            c.Name.Contains("логи"));
        
        if (logChannel != null)
        {
            var embed = new EmbedBuilder()
                .WithDescription(message)
                .WithColor(Color.DarkOrange)
                .WithTimestamp(DateTimeOffset.Now)
                .Build();
            
            await logChannel.SendMessageAsync(embed: embed);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Error logging to mod channel: {ex.Message}");
    }
}

// Подключаемся
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

Console.WriteLine("\n✅ Bot started successfully!");
Console.WriteLine("🎯 Ready to assign roles to new members!");
Console.WriteLine("🛡️ Moderation system: ACTIVE");
Console.WriteLine("📊 Logging: ENABLED");
Console.WriteLine("⏰ Will run for 5h45m, then auto-restart");

// Бесконечное ожидание
await Task.Delay(-1);
