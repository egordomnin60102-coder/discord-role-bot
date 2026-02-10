using Discord;
using Discord.Interactions;

[Group("util", "🛠️ Utilities")]
public class UtilityModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Show all bot commands")]
    public async Task ShowHelp()
    {
        var embed = new EmbedBuilder()
            .WithTitle("🤖 Bot Commands")
            .WithColor(Color.Blue)
            .AddField("🎭 **Role Management**",
                "`/role auto-set` - Set auto-assign role\n" +
                "`/role give` - Give role to user\n" +
                "`/role info` - Show role info\n" +
                "`/role list` - List all roles", false)
            .AddField("⚙️ **Utilities**",
                "`/ping` - Check bot latency\n" +
                "`/server-info` - Server information\n" +
                "`/user-info` - User information", false)
            .WithFooter($"Requested by {Context.User.Username}")
            .Build();
        
        await RespondAsync(embed: embed, ephemeral: true);
    }
    
    [SlashCommand("ping", "Check bot latency")]
    public async Task Ping()
    {
        await RespondAsync($"🏓 Pong! Latency: {Context.Client.Latency}ms");
    }
    
    [SlashCommand("server-info", "Show server information")]
    public async Task ServerInfo()
    {
        var guild = Context.Guild;
        
        var embed = new EmbedBuilder()
            .WithTitle($"🛡️ {guild.Name}")
            .WithThumbnailUrl(guild.IconUrl)
            .WithColor(Color.Green)
            .AddField("👑 Owner", guild.Owner.Mention, true)
            .AddField("📅 Created", $"<t:{guild.CreatedAt.ToUnixTimeSeconds()}:R>", true)
            .AddField("👥 Members", guild.MemberCount.ToString(), true)
            .AddField("📊 Channels", 
                $"Text: {guild.TextChannels.Count}\n" +
                $"Voice: {guild.VoiceChannels.Count}", true)
            .AddField("🎭 Roles", guild.Roles.Count.ToString(), true)
            .AddField("🚀 Boosts", 
                $"Level: {guild.PremiumTier}\n" +
                $"Count: {guild.PremiumSubscriptionCount}", true)
            .WithFooter($"ID: {guild.Id}")
            .Build();
        
        await RespondAsync(embed: embed);
    }
}