using Discord.WebSocket;

public class LoggingService
{
    public async Task LogActionAsync(SocketGuild guild, SocketUser moderator, string action)
    {
        Console.WriteLine($"[MOD LOG] {moderator.Username} -> {action} in {guild.Name}");
        
        // Можно сохранять в базу данных или отправлять в лог-канал
        try
        {
            var logChannel = guild.TextChannels.FirstOrDefault(c => 
                c.Name.Contains("log") || c.Name.Contains("mod-log"));
            
            if (logChannel != null)
            {
                var embed = new Discord.EmbedBuilder()
                    .WithTitle("🛡️ Moderation Action")
                    .AddField("Moderator", moderator.Mention, true)
                    .AddField("Action", action, true)
                    .AddField("Time", $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>", true)
                    .WithColor(Discord.Color.Orange)
                    .Build();
                
                await logChannel.SendMessageAsync(embed: embed);
            }
        }
        catch { }
    }
}