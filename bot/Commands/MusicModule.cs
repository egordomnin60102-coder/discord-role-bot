using Discord;
using Discord.Interactions;
using Discord.WebSocket;

[Group("music", "Music commands")]
public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly MusicService _musicService;

    public MusicModule(MusicService musicService)
    {
        _musicService = musicService;
    }

    [SlashCommand("play", "Play music from YouTube")]
    public async Task Play(
        [Summary("query", "Song name or YouTube URL")] string query)
    {
        await DeferAsync();
        
        var user = Context.User as SocketGuildUser;
        if (user?.VoiceChannel == null)
        {
            await FollowupAsync("❌ You must be in a voice channel!", ephemeral: true);
            return;
        }

        var result = await _musicService.PlayAsync(Context.Guild.Id, user.VoiceChannel.Id, query);
        
        if (result.Success)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🎵 Now Playing")
                .WithDescription($"[{result.Track.Title}]({result.Track.Url})")
                .AddField("Duration", result.Track.Duration, true)
                .AddField("Requested by", Context.User.Mention, true)
                .WithThumbnailUrl(result.Track.Thumbnail)
                .WithColor(Color.Green)
                .Build();
            
            await FollowupAsync(embed: embed);
        }
        else
        {
            await FollowupAsync($"❌ Error: {result.ErrorMessage}", ephemeral: true);
        }
    }

    [SlashCommand("stop", "Stop music and leave voice channel")]
    public async Task Stop()
    {
        await DeferAsync(ephemeral: true);
        
        await _musicService.StopAsync(Context.Guild.Id);
        await FollowupAsync("⏹️ Stopped music and left voice channel", ephemeral: true);
    }

    [SlashCommand("pause", "Pause current track")]
    public async Task Pause()
    {
        await DeferAsync(ephemeral: true);
        
        var result = await _musicService.PauseAsync(Context.Guild.Id);
        
        if (result)
            await FollowupAsync("⏸️ Paused", ephemeral: true);
        else
            await FollowupAsync("❌ Nothing is playing!", ephemeral: true);
    }

    [SlashCommand("resume", "Resume paused track")]
    public async Task Resume()
    {
        await DeferAsync(ephemeral: true);
        
        var result = await _musicService.ResumeAsync(Context.Guild.Id);
        
        if (result)
            await FollowupAsync("▶️ Resumed", ephemeral: true);
        else
            await FollowupAsync("❌ Nothing is paused!", ephemeral: true);
    }

    [SlashCommand("skip", "Skip current track")]
    public async Task Skip()
    {
        await DeferAsync(ephemeral: true);
        
        var result = await _musicService.SkipAsync(Context.Guild.Id);
        
        if (result)
            await FollowupAsync("⏭️ Skipped", ephemeral: true);
        else
            await FollowupAsync("❌ Nothing to skip!", ephemeral: true);
    }

    [SlashCommand("volume", "Set music volume (1-100)")]
    public async Task Volume(
        [Summary("level", "Volume level")] int volume)
    {
        await DeferAsync(ephemeral: true);
        
        if (volume < 1 || volume > 100)
        {
            await FollowupAsync("Volume must be between 1 and 100!", ephemeral: true);
            return;
        }

        await _musicService.SetVolumeAsync(Context.Guild.Id, volume);
        await FollowupAsync($"🔊 Volume set to {volume}%", ephemeral: true);
    }

    [SlashCommand("queue", "Show music queue")]
    public async Task ShowQueue()
    {
        await DeferAsync();
        
        var queue = await _musicService.GetQueueAsync(Context.Guild.Id);
        
        if (queue == null || !queue.Any())
        {
            await FollowupAsync("🎵 Queue is empty!");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🎵 Music Queue")
            .WithColor(Color.Blue);
        
        var description = "";
        for (int i = 0; i < Math.Min(queue.Count, 10); i++)
        {
            description += $"{i + 1}. {queue[i].Title}\n";
        }
        
        if (queue.Count > 10)
            description += $"\n... and {queue.Count - 10} more";
        
        embed.WithDescription(description);
        embed.WithFooter($"Total tracks: {queue.Count}");
        
        await FollowupAsync(embed: embed);
    }

    [SlashCommand("nowplaying", "Show currently playing track")]
    public async Task NowPlaying()
    {
        await DeferAsync();
        
        var track = await _musicService.GetCurrentTrackAsync(Context.Guild.Id);
        
        if (track == null)
        {
            await FollowupAsync("❌ Nothing is playing right now!");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🎵 Now Playing")
            .WithDescription($"[{track.Title}]({track.Url})")
            .AddField("Duration", track.Duration, true)
            .AddField("Uploader", track.Author, true)
            .WithThumbnailUrl(track.Thumbnail)
            .WithColor(Color.Purple)
            .Build();
        
        await FollowupAsync(embed: embed);
    }
}