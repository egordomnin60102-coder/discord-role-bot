using Discord;
using Discord.Interactions;
using Discord.WebSocket;

[Group("mod", "⚖️ Модерация")]
[RequireUserPermission(GuildPermission.ManageMessages)]
[RequireBotPermission(GuildPermission.ManageMessages)]
public class ModerationModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("clear", "Очистить сообщения в канале")]
    public async Task ClearMessages(
        [Summary("количество", "Сколько сообщений удалить (2-100)")] 
        [MinValue(2)] [MaxValue(100)] int amount,
        [Summary("пользователь", "Удалить сообщения только от этого пользователя")] 
        IUser user = null)
    {
        await DeferAsync(ephemeral: true);
        
        var messages = await Context.Channel.GetMessagesAsync(amount + 1).FlattenAsync();
        
        if (user != null)
        {
            messages = messages.Where(m => m.Author.Id == user.Id);
        }
        
        var filteredMessages = messages.Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays <= 14);
        
        if (!filteredMessages.Any())
        {
            await FollowupAsync("❌ Не найдено сообщений для удаления (старее 14 дней)", ephemeral: true);
            return;
        }
        
        await (Context.Channel as ITextChannel).DeleteMessagesAsync(filteredMessages);
        
        var embed = new EmbedBuilder()
            .WithTitle("✅ Сообщения удалены")
            .WithDescription($"Удалено **{filteredMessages.Count()}** сообщений")
            .AddField("Канал", Context.Channel.Mention, true)
            .AddField("Модератор", Context.User.Mention, true)
            .WithColor(Color.Green)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
        
        await FollowupAsync(embed: embed, ephemeral: true);
        
        // Логирование
        Console.WriteLine($"🗑️ Очищено сообщений: {Context.Channel.Name} → {filteredMessages.Count()} ({Context.User.Username})");
    }
    
    [SlashCommand("ban", "Забанить пользователя")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task BanUser(
        [Summary("пользователь", "Пользователь для бана")] SocketGuildUser user,
        [Summary("причина", "Причина бана")] string reason = "Не указана",
        [Summary("удалить_сообщения", "Удалить сообщения за последние дни")] 
        [Choice("Не удалять", "0")]
        [Choice("1 день", "1")]
        [Choice("7 дней", "7")]
        string deleteDays = "0")
    {
        await DeferAsync(ephemeral: true);
        
        if (user.Id == Context.User.Id)
        {
            await FollowupAsync("❌ Нельзя забанить себя!", ephemeral: true);
            return;
        }
        
        if (user.Hierarchy >= Context.Guild.CurrentUser.Hierarchy)
        {
            await FollowupAsync("❌ Не могу забанить этого пользователя (иерархия ролей)", ephemeral: true);
            return;
        }
        
        try
        {
            await user.BanAsync(int.Parse(deleteDays), reason);
            
            var embed = new EmbedBuilder()
                .WithTitle("🔨 Пользователь забанен")
                .WithDescription($"{user.Mention} был забанен на сервере")
                .AddField("Причина", reason, true)
                .AddField("Забанил", Context.User.Mention, true)
                .AddField("Удалено сообщений", deleteDays == "0" ? "Нет" : $"{deleteDays} дней", true)
                .WithColor(Color.Red)
                .WithThumbnailUrl(user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl())
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();
            
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            await FollowupAsync($"❌ Ошибка бана: {ex.Message}", ephemeral: true);
        }
    }
    
    [SlashCommand("kick", "Выгнать пользователя с сервера")]
    [RequireUserPermission(GuildPermission.KickMembers)]
    [RequireBotPermission(GuildPermission.KickMembers)]
    public async Task KickUser(
        [Summary("пользователь", "Пользователь для кика")] SocketGuildUser user,
        [Summary("причина", "Причина кика")] string reason = "Не указана")
    {
        await DeferAsync(ephemeral: true);
        
        if (user.Id == Context.User.Id)
        {
            await FollowupAsync("❌ Нельзя выгнать себя!", ephemeral: true);
            return;
        }
        
        try
        {
            await user.KickAsync(reason);
            
            var embed = new EmbedBuilder()
                .WithTitle("👢 Пользователь выгнан")
                .WithDescription($"{user.Mention} был выгнан с сервера")
                .AddField("Причина", reason, true)
                .AddField("Выгнал", Context.User.Mention, true)
                .WithColor(Color.Orange)
                .WithThumbnailUrl(user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl())
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();
            
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            await FollowupAsync($"❌ Ошибка кика: {ex.Message}", ephemeral: true);
        }
    }
    
    [SlashCommand("timeout", "Выдать тайм-аут пользователю")]
    public async Task TimeoutUser(
        [Summary("пользователь", "Пользователь для тайм-аута")] SocketGuildUser user,
        [Summary("время", "Длительность тайм-аута")] 
        [Choice("5 минут", "5")]
        [Choice("10 минут", "10")]
        [Choice("1 час", "60")]
        [Choice("1 день", "1440")]
        [Choice("1 неделя", "10080")]
        int minutes,
        [Summary("причина", "Причина тайм-аута")] string reason = "Не указана")
    {
        await DeferAsync(ephemeral: true);
        
        var duration = TimeSpan.FromMinutes(minutes);
        
        try
        {
            await user.SetTimeOutAsync(duration);
            
            var embed = new EmbedBuilder()
                .WithTitle("⏰ Тайм-аут выдан")
                .WithDescription($"{user.Mention} получил тайм-аут на {minutes} минут")
                .AddField("До", $"<t:{(DateTimeOffset.UtcNow + duration).ToUnixTimeSeconds()}:R>", true)
                .AddField("Причина", reason, true)
                .AddField("Выдал", Context.User.Mention, true)
                .WithColor(Color.LightOrange)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();
            
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            await FollowupAsync($"❌ Ошибка тайм-аута: {ex.Message}", ephemeral: true);
        }
    }
}