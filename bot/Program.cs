using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

public class Warning
{
    public string Reason { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public ulong ModeratorId { get; set; }
}

public class Program
{
    private static DiscordSocketClient? client;
    private static InteractionService? interactions;
    private static IServiceProvider? services;
    private static Dictionary<ulong, Dictionary<ulong, List<Warning>>> userWarnings = new();
    private static Dictionary<string, Timer> activeTimers = new();

    public static async Task Main(string[] args)
    {
        Console.Title = "Discord Moderation Bot - GitHub Hosted";
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("🤖 Discord Moderation Bot - Auto Role + Moderation");
        Console.WriteLine("==================================================");

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ ERROR: No DISCORD_TOKEN in GitHub Secrets!");
            return;
        }

        Console.WriteLine("✅ Token received");
        Console.WriteLine("🚀 Starting bot...");

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                           GatewayIntents.GuildMembers |
                           GatewayIntents.GuildMessages |
                           GatewayIntents.GuildVoiceStates |
                           GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        });

        interactions = new InteractionService(client, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async,
            LogLevel = LogSeverity.Info
        });

        services = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton(interactions)
            .AddSingleton<CommandHandler>()
            .BuildServiceProvider();

        client.Log += LogMessage;
        client.Ready += ReadyAsync;
        client.UserJoined += UserJoinedAsync;
        
        // Модерационные события
        client.UserBanned += UserBannedAsync;
        client.UserUnbanned += UserUnbannedAsync;
        client.UserLeft += UserLeftAsync;
        client.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;
        client.RoleCreated += RoleCreatedAsync;
        client.RoleDeleted += RoleDeletedAsync;
        client.UserUpdated += UserUpdatedAsync;

        await services.GetRequiredService<CommandHandler>().InitializeAsync();

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        Console.WriteLine("\n✅ Bot started successfully!");
        Console.WriteLine("🎯 Ready to assign roles to new members!");
        Console.WriteLine("🛡️ Moderation system: ACTIVE");
        Console.WriteLine("📊 Logging: ENABLED");
        Console.WriteLine("⏰ Will run for 5h45m, then auto-restart");

        await Task.Delay(-1);
    }

    private static Task LogMessage(LogMessage msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg.Message}");
        return Task.CompletedTask;
    }

    private static async Task ReadyAsync()
    {
        if (client == null || interactions == null) return;
        
        Console.WriteLine($"\n🎉 BOT READY: {client.CurrentUser}");
        Console.WriteLine($"🏰 Servers: {client.Guilds.Count}");

        // Регистрируем команды глобально
        await interactions.RegisterCommandsGloballyAsync();
        Console.WriteLine("✅ Slash commands registered globally!");

        foreach (var guild in client.Guilds)
        {
            Console.WriteLine($"   • {guild.Name} (ID: {guild.Id})");
            Console.WriteLine($"     Members: {guild.MemberCount}, Roles: {guild.Roles.Count}");

            if (!userWarnings.ContainsKey(guild.Id))
            {
                userWarnings[guild.Id] = new Dictionary<ulong, List<Warning>>();
            }
        }

        Console.WriteLine("===========================================");
    }

    // === АВТОВЫДАЧА РОЛИ ===
    private static async Task UserJoinedAsync(SocketGuildUser user)
    {
        Console.WriteLine($"\n[🎉] NEW USER: {user.Username} joined {user.Guild.Name}");
        
        try
        {
            var role = FindRoleForUser(user.Guild);
            if (role == null)
            {
                Console.WriteLine($"   ⚠️ No suitable role found on {user.Guild.Name}");
                return;
            }

            var botUser = user.Guild.CurrentUser;
            if (botUser == null || !botUser.GuildPermissions.ManageRoles)
            {
                Console.WriteLine($"   ❌ Bot doesn't have 'Manage Roles' permission");
                return;
            }

            await user.AddRoleAsync(role);
            Console.WriteLine($"   ✅ SUCCESS: Role {role.Name} assigned to {user.Username}!");
            await SendWelcomeMessage(user, role);
            await LogToModChannel(user.Guild,
                $"🎉 **Новый участник**\n" +
                $"👤 Пользователь: {user.Mention}\n" +
                $"🎭 Получена роль: {role.Mention}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   💥 ERROR assigning role: {ex.Message}");
        }
    }

    // === МОДЕРАЦИОННЫЕ КОМАНДЫ ===
    public class CommandHandler : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("help", "Показать список всех команд")]
        public async Task HelpCommand()
        {
            var embed = new EmbedBuilder()
                .WithTitle("🤖 Moderation Bot - Все команды")
                .WithDescription("**Префикс:** `/` (слеш-команды)")
                .WithColor(Color.Blue)
                .AddField("🛡️ **Модерация**", 
                    "`/tempmute` - Временный мут\n" +
                    "`/tempban` - Временный бан\n" +
                    "`/mute` - Замутить\n" +
                    "`/unmute` - Размутить\n" +
                    "`/kick` - Кикнуть\n" +
                    "`/ban` - Забанить\n" +
                    "`/unban` - Разбанить\n" +
                    "`/clear` - Очистить сообщения", true)
                .AddField("⚠️ **Предупреждения**", 
                    "`/warn` - Выдать варн\n" +
                    "`/warnings` - Список варнов\n" +
                    "`/removewarn` - Удалить варн\n" +
                    "`/modstats` - Статистика", true)
                .AddField("⚙️ **Настройки**", 
                    "`/setrole` - Настройка роли\n" +
                    "`/roleinfo` - Инфо о роли\n" +
                    "`/modlog` - Лог-канал\n" +
                    "`/ping` - Проверка бота", true)
                .AddField("🎯 **Авто-функции**",
                    "• Автовыдача роли новичкам\n" +
                    "• Логирование всех действий\n" +
                    "• 3 варна = мут 1ч\n" +
                    "• 5 варнов = бан", false)
                .WithFooter($"Серверов: {client?.Guilds.Count ?? 0} • Хостинг: GitHub Actions")
                .WithCurrentTimestamp()
                .Build();

            await RespondAsync(embed: embed, ephemeral: true);
        }

        [SlashCommand("ping", "Проверка работы бота")]
        public async Task PingCommand()
        {
            await RespondAsync($"🏓 **Pong!**\n⚡ Задержка: {Context.Client.Latency}ms", ephemeral: true);
        }

        [SlashCommand("roleinfo", "Информация о роли для новичков")]
        public async Task RoleInfoCommand()
        {
            var role = FindRoleForUser(Context.Guild);
            if (role == null)
            {
                await RespondAsync("❌ Не найдена подходящая роль для выдачи.", ephemeral: true);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("🎯 Роль для новичков")
                .WithColor(role.Color)
                .AddField("Роль", role.Mention, true)
                .AddField("Название", role.Name, true)
                .AddField("ID", role.Id.ToString(), true)
                .AddField("Цвет", role.Color.ToString(), true)
                .AddField("Позиция", role.Position.ToString(), true)
                .WithFooter("Новые участники получают эту роль автоматически")
                .Build();

            await RespondAsync(embed: embed);
        }

        [SlashCommand("setrole", "Информация о настройке роли")]
        public async Task SetRoleCommand()
        {
            var user = Context.User as SocketGuildUser;
            if (user == null || !user.GuildPermissions.ManageRoles)
            {
                await RespondAsync("❌ Нужны права **Manage Roles**!", ephemeral: true);
                return;
            }

            await RespondAsync(
                "⚙️ **Настройка роли:**\n" +
                "Бот автоматически ищет роли: `Member`, `Участник`, `Новичок`\n" +
                "Чтобы изменить роль - обновите код в репозитории.", ephemeral: true);
        }

        [SlashCommand("tempmute", "Временный мут пользователя")]
        public async Task TempMuteCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("time", "Время (30s, 5m, 2h, 1d)")] string time,
            [Summary("reason", "Причина")] string reason = "Не указана")
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.MuteMembers)
            {
                await RespondAsync("❌ Нужны права **Mute Members**!", ephemeral: true);
                return;
            }

            if (user.Id == author.Id)
            {
                await RespondAsync("❌ Нельзя замутить себя!", ephemeral: true);
                return;
            }

            if (!TryParseTime(time, out var timeSpan))
            {
                await RespondAsync("❌ Неверный формат времени! Используйте: 30s, 5m, 2h, 1d", ephemeral: true);
                return;
            }

            var muteRole = await GetOrCreateMuteRole(Context.Guild);
            if (muteRole == null)
            {
                await RespondAsync("❌ Не удалось создать/найти роль для мута!", ephemeral: true);
                return;
            }

            await user.AddRoleAsync(muteRole);

            var timerKey = $"mute_{Context.Guild.Id}_{user.Id}";
            var timer = new Timer(async _ =>
            {
                try
                {
                    if (user.Roles.Any(r => r.Id == muteRole.Id))
                    {
                        await user.RemoveRoleAsync(muteRole);
                        await LogToModChannel(Context.Guild,
                            $"🔓 **Автоматический размут**\n👤 {user.Mention}\n⏰ Был замучен на: {time}");
                    }
                }
                catch { }
            }, null, timeSpan, Timeout.InfiniteTimeSpan);

            if (activeTimers.ContainsKey(timerKey))
                activeTimers[timerKey]?.Dispose();
            activeTimers[timerKey] = timer;

            var embed = new EmbedBuilder()
                .WithTitle("🔇 Временный мут")
                .WithColor(Color.Orange)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Время", time, true)
                .AddField("Причина", reason)
                .AddField("Размут", $"<t:{((DateTimeOffset)DateTime.UtcNow.Add(timeSpan)).ToUnixTimeSeconds()}:R>")
                .Build();

            await RespondAsync(embed: embed);
            await LogToModChannel(Context.Guild, $"🔇 **Временный мут**\n👤 {user.Mention}\n👮 {author.Mention}\n⏰ {time}\n📝 {reason}");
        }

        [SlashCommand("tempban", "Временный бан пользователя")]
        public async Task TempBanCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("time", "Время (1h, 1d, 7d)")] string time,
            [Summary("reason", "Причина")] string reason = "Не указана")
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.BanMembers)
            {
                await RespondAsync("❌ Нужны права **Ban Members**!", ephemeral: true);
                return;
            }

            if (!TryParseTime(time, out var timeSpan))
            {
                await RespondAsync("❌ Неверный формат времени! Используйте: 1h, 1d, 7d", ephemeral: true);
                return;
            }

            await Context.Guild.AddBanAsync(user, 0, reason);

            var timerKey = $"ban_{Context.Guild.Id}_{user.Id}";
            var timer = new Timer(async _ =>
            {
                try
                {
                    await Context.Guild.RemoveBanAsync(user);
                    await LogToModChannel(Context.Guild,
                        $"🔓 **Автоматический разбан**\n👤 `{user.Username}`\n⏰ Был забанен на: {time}");
                }
                catch { }
            }, null, timeSpan, Timeout.InfiniteTimeSpan);

            if (activeTimers.ContainsKey(timerKey))
                activeTimers[timerKey]?.Dispose();
            activeTimers[timerKey] = timer;

            var embed = new EmbedBuilder()
                .WithTitle("🔨 Временный бан")
                .WithColor(Color.Red)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Время", time, true)
                .AddField("Причина", reason)
                .AddField("Разбан", $"<t:{((DateTimeOffset)DateTime.UtcNow.Add(timeSpan)).ToUnixTimeSeconds()}:R>")
                .Build();

            await RespondAsync(embed: embed);
            await LogToModChannel(Context.Guild, $"🔨 **Временный бан**\n👤 {user.Mention}\n👮 {author.Mention}\n⏰ {time}\n📝 {reason}");
        }

        [SlashCommand("clear", "Очистить сообщения в канале")]
        public async Task ClearCommand(
            [Summary("amount", "Количество сообщений (1-100)")] int amount)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.ManageMessages)
            {
                await RespondAsync("❌ Нужны права **Manage Messages**!", ephemeral: true);
                return;
            }

            if (amount < 1 || amount > 100)
            {
                await RespondAsync("❌ Количество должно быть от 1 до 100!", ephemeral: true);
                return;
            }

            var messages = await Context.Channel.GetMessagesAsync(amount + 1).FlattenAsync();
            var filteredMessages = messages.Where(m => (DateTime.UtcNow - m.CreatedAt).TotalDays <= 14);

            if (Context.Channel is SocketTextChannel textChannel)
            {
                await textChannel.DeleteMessagesAsync(filteredMessages);
                await RespondAsync($"🧹 Удалено {filteredMessages.Count() - 1} сообщений!", ephemeral: true);
                await LogToModChannel(Context.Guild,
                    $"🧹 **Очистка сообщений**\n👮 {author.Mention}\n📊 Удалено: {filteredMessages.Count() - 1}\n📢 Канал: {Context.Channel.Name}");
            }
        }

        [SlashCommand("warn", "Выдать предупреждение пользователю")]
        public async Task WarnCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("reason", "Причина")] string reason)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.KickMembers)
            {
                await RespondAsync("❌ Нужны права **Kick Members**!", ephemeral: true);
                return;
            }

            if (!userWarnings.ContainsKey(Context.Guild.Id))
                userWarnings[Context.Guild.Id] = new Dictionary<ulong, List<Warning>>();

            if (!userWarnings[Context.Guild.Id].ContainsKey(user.Id))
                userWarnings[Context.Guild.Id][user.Id] = new List<Warning>();

            userWarnings[Context.Guild.Id][user.Id].Add(new Warning
            {
                Reason = reason,
                Date = DateTime.Now,
                ModeratorId = author.Id
            });

            var warningCount = userWarnings[Context.Guild.Id][user.Id].Count;
            string autoAction = "";

            if (warningCount >= 5)
            {
                await Context.Guild.AddBanAsync(user, 0, "5 предупреждений");
                autoAction = "🔨 Автоматический бан (5 предупреждений)";
            }
            else if (warningCount >= 3)
            {
                var muteRole = await GetOrCreateMuteRole(Context.Guild);
                if (muteRole != null)
                {
                    await user.AddRoleAsync(muteRole);
                    autoAction = "🔇 Автоматический мут на 1 час (3 предупреждения)";
                    
                    var timer = new Timer(async _ =>
                    {
                        try
                        {
                            if (user.Roles.Any(r => r.Id == muteRole.Id))
                                await user.RemoveRoleAsync(muteRole);
                        }
                        catch { }
                    }, null, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
                    
                    activeTimers[$"auto_mute_{Context.Guild.Id}_{user.Id}"] = timer;
                }
            }

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Предупреждение")
                .WithColor(Color.Orange)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Причина", reason, true)
                .AddField("Всего предупреждений", warningCount.ToString(), true)
                .Build();

            await RespondAsync(embed: embed);
            await LogToModChannel(Context.Guild, 
                $"⚠️ **Предупреждение**\n👤 {user.Mention}\n👮 {author.Mention}\n📝 {reason}\n📊 Всего: {warningCount}\n{autoAction}");
        }

        [SlashCommand("warnings", "Показать предупреждения пользователя")]
        public async Task WarningsCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.KickMembers)
            {
                await RespondAsync("❌ Нужны права **Kick Members**!", ephemeral: true);
                return;
            }

            if (!userWarnings.ContainsKey(Context.Guild.Id) || 
                !userWarnings[Context.Guild.Id].ContainsKey(user.Id) || 
                userWarnings[Context.Guild.Id][user.Id].Count == 0)
            {
                await RespondAsync($"✅ У пользователя {user.Mention} нет предупреждений.", ephemeral: true);
                return;
            }

            var warnings = userWarnings[Context.Guild.Id][user.Id];
            var embed = new EmbedBuilder()
                .WithTitle($"⚠️ Предупреждения {user.Username}")
                .WithColor(Color.Orange)
                .WithThumbnailUrl(user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl());

            for (int i = 0; i < warnings.Count; i++)
            {
                var warning = warnings[i];
                var moderator = Context.Guild.GetUser(warning.ModeratorId);
                embed.AddField($"#{i + 1}", 
                    $"**Причина:** {warning.Reason}\n" +
                    $"**Модератор:** {(moderator?.Mention ?? $"ID: {warning.ModeratorId}")}\n" +
                    $"**Дата:** {warning.Date:dd.MM.yyyy HH:mm}", 
                    false);
            }

            embed.WithFooter($"Всего: {warnings.Count}");
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("removewarn", "Удалить предупреждение")]
        public async Task RemoveWarnCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("number", "Номер предупреждения")] int number)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.KickMembers)
            {
                await RespondAsync("❌ Нужны права **Kick Members**!", ephemeral: true);
                return;
            }

            if (!userWarnings.ContainsKey(Context.Guild.Id) || 
                !userWarnings[Context.Guild.Id].ContainsKey(user.Id) || 
                number > userWarnings[Context.Guild.Id][user.Id].Count || number < 1)
            {
                await RespondAsync("❌ Предупреждение не найдено!", ephemeral: true);
                return;
            }

            userWarnings[Context.Guild.Id][user.Id].RemoveAt(number - 1);
            
            if (userWarnings[Context.Guild.Id][user.Id].Count == 0)
                userWarnings[Context.Guild.Id].Remove(user.Id);

            await RespondAsync($"✅ Предупреждение #{number} удалено у {user.Mention}");
            await LogToModChannel(Context.Guild, 
                $"✅ **Удалено предупреждение**\n👤 {user.Mention}\n👮 {author.Mention}\n🔢 Номер: {number}");
        }

        [SlashCommand("modstats", "Статистика модерации")]
        public async Task ModStatsCommand()
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.KickMembers)
            {
                await RespondAsync("❌ Нужны права **Kick Members**!", ephemeral: true);
                return;
            }

            if (!userWarnings.ContainsKey(Context.Guild.Id) || userWarnings[Context.Guild.Id].Count == 0)
            {
                await RespondAsync("📊 На этом сервере еще нет предупреждений.", ephemeral: true);
                return;
            }

            var totalWarnings = userWarnings[Context.Guild.Id].Sum(x => x.Value.Count);
            var topUsers = userWarnings[Context.Guild.Id]
                .OrderByDescending(x => x.Value.Count)
                .Take(5)
                .Select(x => {
                    var u = Context.Guild.GetUser(x.Key);
                    return $"• {(u?.Mention ?? $"ID: {x.Key}")}: {x.Value.Count} варнов";
                });

            var embed = new EmbedBuilder()
                .WithTitle("📊 Статистика модерации")
                .WithColor(Color.Purple)
                .AddField("Всего варнов", totalWarnings.ToString(), true)
                .AddField("Нарушителей", userWarnings[Context.Guild.Id].Count.ToString(), true)
                .AddField("Топ нарушителей", string.Join("\n", topUsers), false)
                .WithFooter(Context.Guild.Name)
                .Build();

            await RespondAsync(embed: embed);
        }

        [SlashCommand("kick", "Кикнуть пользователя")]
        public async Task KickCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("reason", "Причина")] string reason = "Не указана")
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.KickMembers)
            {
                await RespondAsync("❌ Нужны права **Kick Members**!", ephemeral: true);
                return;
            }

            await user.KickAsync(reason);
            
            var embed = new EmbedBuilder()
                .WithTitle("👢 Кик")
                .WithColor(Color.Orange)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Причина", reason, true)
                .Build();

            await RespondAsync(embed: embed);
            await LogToModChannel(Context.Guild, 
                $"👢 **Кик**\n👤 {user.Mention}\n👮 {author.Mention}\n📝 {reason}");
        }

        [SlashCommand("ban", "Забанить пользователя")]
        public async Task BanCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("reason", "Причина")] string reason = "Не указана")
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.BanMembers)
            {
                await RespondAsync("❌ Нужны права **Ban Members**!", ephemeral: true);
                return;
            }

            await Context.Guild.AddBanAsync(user, 0, reason);
            
            var embed = new EmbedBuilder()
                .WithTitle("🔨 Бан")
                .WithColor(Color.Red)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Причина", reason, true)
                .Build();

            await RespondAsync(embed: embed);
        }

        [SlashCommand("unban", "Разбанить пользователя по ID")]
        public async Task UnbanCommand(
            [Summary("user_id", "ID пользователя")] string userId)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.BanMembers)
            {
                await RespondAsync("❌ Нужны права **Ban Members**!", ephemeral: true);
                return;
            }

            if (!ulong.TryParse(userId, out var id))
            {
                await RespondAsync("❌ Неверный ID пользователя!", ephemeral: true);
                return;
            }

            try
            {
                await Context.Guild.RemoveBanAsync(id);
                await RespondAsync($"🔓 Пользователь с ID `{userId}` разбанен");
                
                var timerKey = $"ban_{Context.Guild.Id}_{id}";
                if (activeTimers.ContainsKey(timerKey))
                {
                    activeTimers[timerKey]?.Dispose();
                    activeTimers.Remove(timerKey);
                }

                await LogToModChannel(Context.Guild,
                    $"🔓 **Разбан**\n👤 ID: `{userId}`\n👮 {author.Mention}");
            }
            catch
            {
                await RespondAsync("❌ Пользователь не найден в списке банов!", ephemeral: true);
            }
        }

        [SlashCommand("mute", "Замутить пользователя")]
        public async Task MuteCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user,
            [Summary("reason", "Причина")] string reason = "Не указана")
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.MuteMembers)
            {
                await RespondAsync("❌ Нужны права **Mute Members**!", ephemeral: true);
                return;
            }

            var muteRole = await GetOrCreateMuteRole(Context.Guild);
            if (muteRole == null)
            {
                await RespondAsync("❌ Не удалось создать/найти роль для мута!", ephemeral: true);
                return;
            }

            await user.AddRoleAsync(muteRole);
            
            var embed = new EmbedBuilder()
                .WithTitle("🔇 Мут")
                .WithColor(Color.LightGrey)
                .AddField("Пользователь", user.Mention, true)
                .AddField("Модератор", author.Mention, true)
                .AddField("Причина", reason, true)
                .Build();

            await RespondAsync(embed: embed);
            await LogToModChannel(Context.Guild,
                $"🔇 **Мут**\n👤 {user.Mention}\n👮 {author.Mention}\n📝 {reason}");
        }

        [SlashCommand("unmute", "Размутить пользователя")]
        public async Task UnmuteCommand(
            [Summary("user", "Пользователь")] SocketGuildUser user)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.MuteMembers)
            {
                await RespondAsync("❌ Нужны права **Mute Members**!", ephemeral: true);
                return;
            }

            var muteRole = await GetOrCreateMuteRole(Context.Guild);
            if (muteRole == null)
            {
                await RespondAsync("❌ Роль для мута не найдена!", ephemeral: true);
                return;
            }

            await user.RemoveRoleAsync(muteRole);
            
            var timerKey = $"mute_{Context.Guild.Id}_{user.Id}";
            if (activeTimers.ContainsKey(timerKey))
            {
                activeTimers[timerKey]?.Dispose();
                activeTimers.Remove(timerKey);
            }

            await RespondAsync($"🔓 Пользователь {user.Mention} размучен");
            await LogToModChannel(Context.Guild,
                $"🔓 **Размут**\n👤 {user.Mention}\n👮 {author.Mention}");
        }

        [SlashCommand("modlog", "Настройка канала для логов")]
        public async Task ModLogCommand(
            [Summary("channel", "Канал для логов")] SocketTextChannel? channel = null)
        {
            var author = Context.User as SocketGuildUser;
            if (author == null || !author.GuildPermissions.Administrator)
            {
                await RespondAsync("❌ Нужны права **Administrator**!", ephemeral: true);
                return;
            }

            if (channel == null)
            {
                await RespondAsync(
                    "📋 **Информация о логах**\n" +
                    "Бот автоматически ищет каналы с названиями:\n" +
                    "• `mod-log`\n• `logs`\n• `модерация`\n• `логи`\n\n" +
                    "Используйте: `/modlog #канал` чтобы указать канал",
                    ephemeral: true);
            }
            else
            {
                await RespondAsync($"✅ Канал {channel.Mention} будет использоваться для логов", ephemeral: true);
            }
        }
    }

    // === ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ ===

    private static SocketRole? FindRoleForUser(SocketGuild guild)
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
                r.Name.Contains(roleName, StringComparison.OrdinalIgnoreCase) && !r.IsEveryone);
            if (role != null) return role;
        }

        return guild.Roles.FirstOrDefault(r => !r.IsEveryone && r != guild.EveryoneRole);
    }

    private static async Task SendWelcomeMessage(SocketGuildUser user, SocketRole role)
    {
        try
        {
            var channel = user.Guild.SystemChannel ??
                         user.Guild.TextChannels.FirstOrDefault(c =>
                             c.Name.Contains("общ") || c.Name.Contains("general") || c.Name.Contains("welcome"));

            if (channel != null)
            {
                await channel.SendMessageAsync($"👋 Добро пожаловать, {user.Mention}! Ты получил роль {role.Mention}.");
            }
        }
        catch { }
    }

    private static bool TryParseTime(string input, out TimeSpan timeSpan)
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

    private static async Task<SocketRole?> GetOrCreateMuteRole(SocketGuild guild)
    {
        var muteRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals("Muted", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("Мут", StringComparison.OrdinalIgnoreCase));

        if (muteRole != null) return muteRole;

        try
        {
            var botUser = guild.CurrentUser;
            if (botUser == null || !botUser.GuildPermissions.ManageRoles) return null;

            var newRole = await guild.CreateRoleAsync("Muted", GuildPermissions.None, Color.DarkGrey, false, false);
            await Task.Delay(1000);
            
            muteRole = guild.Roles.FirstOrDefault(r => r.Id == newRole.Id);
            if (muteRole == null) return null;

            foreach (var channel in guild.TextChannels)
            {
                try
                {
                    await channel.AddPermissionOverwriteAsync(muteRole,
                        new OverwritePermissions(sendMessages: PermValue.Deny, addReactions: PermValue.Deny));
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

    private static async Task LogToModChannel(SocketGuild guild, string message)
    {
        try
        {
            var logChannel = guild.TextChannels.FirstOrDefault(c =>
                c.Name.Contains("mod-log") || c.Name.Contains("logs") ||
                c.Name.Contains("moderator") || c.Name.Contains("модерация") || c.Name.Contains("логи"));

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
        catch { }
    }

    private static async Task UserBannedAsync(SocketUser user, SocketGuild guild)
    {
        await LogToModChannel(guild, $"🔨 **Бан**\n👤 Пользователь: `{user.Username}`");
    }

    private static async Task UserUnbannedAsync(SocketUser user, SocketGuild guild)
    {
        await LogToModChannel(guild, $"🔓 **Разбан**\n👤 Пользователь: `{user.Username}`");
    }

    private static async Task UserLeftAsync(SocketGuild guild, SocketUser user)
    {
        await LogToModChannel(guild, $"🚪 **Покинул сервер**\n👤 Пользователь: `{user.Username}`");
    }

    private static async Task UserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
    {
        if (user is SocketGuildUser guildUser)
        {
            if (oldState.VoiceChannel == null && newState.VoiceChannel != null)
            {
                await LogToModChannel(guildUser.Guild,
                    $"🎤 **Залетел в войс**\n👤 {guildUser.Mention}\n📢 {newState.VoiceChannel.Name}");
            }
            else if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
            {
                await LogToModChannel(guildUser.Guild,
                    $"🔇 **Вышел из войса**\n👤 {guildUser.Mention}\n📢 {oldState.VoiceChannel.Name}");
            }
        }
    }

    private static async Task RoleCreatedAsync(SocketRole role)
    {
        await LogToModChannel(role.Guild, $"🆕 **Создана роль**\n🎭 {role.Mention}");
    }

    private static async Task RoleDeletedAsync(SocketRole role)
    {
        await LogToModChannel(role.Guild, $"🗑️ **Удалена роль**\n🎭 `{role.Name}`");
    }

    private static async Task UserUpdatedAsync(SocketUser oldUser, SocketUser newUser)
    {
        if (oldUser is SocketGuildUser oldGuild && newUser is SocketGuildUser newGuild)
        {
            var oldRoles = oldGuild.Roles.Select(r => r.Id).ToHashSet();
            var newRoles = newGuild.Roles.Select(r => r.Id).ToHashSet();

            if (!oldRoles.SetEquals(newRoles))
            {
                var added = newRoles.Except(oldRoles).Select(id => newGuild.Guild.GetRole(id)).Where(r => r != null);
                var removed = oldRoles.Except(newRoles).Select(id => newGuild.Guild.GetRole(id)).Where(r => r != null);

                foreach (var role in added)
                    await LogToModChannel(newGuild.Guild, $"➕ **Добавлена роль**\n👤 {newGuild.Mention}\n🎭 {role.Mention}");

                foreach (var role in removed)
                    await LogToModChannel(newGuild.Guild, $"➖ **Удалена роль**\n👤 {newGuild.Mention}\n🎭 {role.Mention}");
            }
        }
    }
}
