using Discord;
using Discord.Interactions;
using Discord.WebSocket;

[Group("role", "🎭 Role management")]
public class RoleModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("auto-set", "Set auto-assign role for new members")]
    [RequireUserPermission(GuildPermission.ManageRoles)]
    [RequireBotPermission(GuildPermission.ManageRoles)]
    public async Task SetAutoRole(
        [Summary("role", "Role to auto-assign")] SocketRole role)
    {
        await DeferAsync(ephemeral: true);
        
        // Check role hierarchy
        var botUser = Context.Guild.CurrentUser;
        var highestBotRole = botUser.Roles.OrderByDescending(r => r.Position).FirstOrDefault();
        
        if (highestBotRole == null || highestBotRole.Position <= role.Position)
        {
            await FollowupAsync(
                $"❌ My role ({highestBotRole?.Mention}) must be HIGHER than {role.Mention}!\n" +
                "Drag my role higher in server settings → Roles.",
                ephemeral: true
            );
            return;
        }
        
        await FollowupAsync(
            $"✅ Auto-role set to {role.Mention}\n" +
            "New members will receive this role automatically.",
            ephemeral: true
        );
        
        Console.WriteLine($"⚙️ Auto-role set: {Context.Guild.Name} → {role.Name}");
    }
    
    [SlashCommand("give", "Give role to user")]
    [RequireUserPermission(GuildPermission.ManageRoles)]
    [RequireBotPermission(GuildPermission.ManageRoles)]
    public async Task GiveRole(
        [Summary("user", "User to give role")] SocketGuildUser user,
        [Summary("role", "Role to give")] SocketRole role)
    {
        await DeferAsync(ephemeral: true);
        
        if (user.Roles.Any(r => r.Id == role.Id))
        {
            await FollowupAsync($"ℹ️ {user.Mention} already has {role.Mention}", ephemeral: true);
            return;
        }
        
        try
        {
            await user.AddRoleAsync(role);
            await FollowupAsync(
                $"✅ {role.Mention} given to {user.Mention}",
                ephemeral: true
            );
        }
        catch (Exception ex)
        {
            await FollowupAsync($"❌ Error: {ex.Message}", ephemeral: true);
        }
    }
    
    [SlashCommand("info", "Show role information")]
    public async Task RoleInfo(
        [Summary("role", "Role to check")] SocketRole role)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"🎭 {role.Name}")
            .WithColor(role.Color)
            .AddField("ID", role.Id, true)
            .AddField("Position", role.Position, true)
            .AddField("Members", role.Members.Count(), true)
            .AddField("Color", role.Color.ToString(), true)
            .AddField("Mentionable", role.IsMentionable ? "Yes" : "No", true)
            .AddField("Hoisted", role.IsHoisted ? "Yes" : "No", true)
            .AddField("Created", $"<t:{((DateTimeOffset)role.CreatedAt).ToUnixTimeSeconds()}:R>", false)
            .WithFooter($"Requested by {Context.User.Username}")
            .Build();
        
        await RespondAsync(embed: embed);
    }
    
    [SlashCommand("list", "List all server roles")]
    public async Task ListRoles()
    {
        await DeferAsync();
        
        var roles = Context.Guild.Roles
            .Where(r => !r.IsEveryone)
            .OrderByDescending(r => r.Position)
            .Take(15);
        
        var embed = new EmbedBuilder()
            .WithTitle($"🎭 Roles on {Context.Guild.Name}")
            .WithColor(Color.Blue);
        
        var description = "";
        foreach (var role in roles)
        {
            description += $"{role.Mention} - {role.Members.Count()} members\n";
        }
        
        embed.WithDescription(description);
        embed.WithFooter($"Total roles: {Context.Guild.Roles.Count}");
        
        await FollowupAsync(embed: embed);
    }
}