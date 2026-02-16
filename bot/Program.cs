using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

public class Program
{
    private static DiscordSocketClient? client;
    private static InteractionService? interactions;
    private static IServiceProvider? services;
    private static ConcurrentDictionary<ulong, MusicPlayer> musicPlayers = new();
    private static HttpClient httpClient = new();
    private static readonly string[] InvidiousInstances = new[]
    {
        "https://invidious.projectsegfau.lt",
        "https://yewtu.be",
        "https://inv.riverside.rocks",
        "https://invidious.snopyta.org",
        "https://vid.puffyan.us",
        "https://invidious.nerdvpn.de",
        "https://inv.bp.projectsegfau.lt"
    };

    public static async Task Main(string[] args)
    {
        Console.Title = "Discord Music Bot - GitHub Hosted";
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("🎵 Discord Music Bot - GitHub Actions");
        Console.WriteLine("=======================================");

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ ERROR: No DISCORD_TOKEN in GitHub Secrets!");
            return;
        }

        Console.WriteLine("✅ Token received");
        Console.WriteLine("🚀 Starting bot...");

        services = new ServiceCollection()
            .AddSingleton<DiscordSocketClient>()
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
            .BuildServiceProvider();

        client = services.GetRequiredService<DiscordSocketClient>();
        interactions = services.GetRequiredService<InteractionService>();

        client.Log += LogMessage;
        client.Ready += ReadyAsync;
        client.InteractionCreated += InteractionCreatedAsync;
        client.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        Console.WriteLine("\n✅ Bot started successfully!");
        Console.WriteLine("🎵 Music system: ACTIVE");
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
        if (client == null) return;
        
        Console.WriteLine($"\n🎉 BOT READY: {client.CurrentUser}");
        Console.WriteLine($"🏰 Servers: {client.Guilds.Count}");

        await interactions.AddModuleAsync<MusicCommands>(services);
        await interactions.RegisterCommandsGloballyAsync();
        Console.WriteLine("✅ Slash commands registered globally!");

        foreach (var guild in client.Guilds)
        {
            Console.WriteLine($"   • {guild.Name} (ID: {guild.Id})");
            musicPlayers[guild.Id] = new MusicPlayer();
        }

        Console.WriteLine("===========================================");
    }

    private static async Task InteractionCreatedAsync(SocketInteraction interaction)
    {
        if (interactions == null || client == null) return;
        
        var ctx = new SocketInteractionContext(client, interaction);
        await interactions.ExecuteCommandAsync(ctx, services);
    }

    private static async Task UserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
    {
        if (user.IsBot) return;

        if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
        {
            var guild = (oldState.VoiceChannel as SocketGuildChannel)?.Guild;
            if (guild == null) return;

            var voiceChannel = oldState.VoiceChannel;
            if (voiceChannel.ConnectedUsers.Count == 1 && voiceChannel.ConnectedUsers.Any(x => x.Id == client?.CurrentUser?.Id))
            {
                _ = Task.Delay(30000).ContinueWith(async _ =>
                {
                    var currentChannel = guild.VoiceChannels.FirstOrDefault(x => x.Id == voiceChannel.Id);
                    if (currentChannel != null && currentChannel.ConnectedUsers.Count == 1 && 
                        currentChannel.ConnectedUsers.Any(x => x.Id == client?.CurrentUser?.Id))
                    {
                        await currentChannel.DisconnectAsync();
                        if (musicPlayers.TryGetValue(guild.Id, out var player))
                        {
                            player.Stop();
                        }
                    }
                });
            }
        }
    }

    public class MusicPlayer
    {
        public Queue<SongInfo> Queue { get; set; } = new();
        public bool IsPlaying { get; set; } = false;
        public bool Loop { get; set; } = false;
        public IVoiceChannel? VoiceChannel { get; set; }
        public ITextChannel? TextChannel { get; set; }
        public Process? FfmpegProcess { get; set; }
        public IAudioClient? AudioClient { get; set; }
        public SongInfo? CurrentSong { get; set; }
        public CancellationTokenSource? PlaybackCts { get; set; }

        public void Stop()
        {
            IsPlaying = false;
            CurrentSong = null;
            
            try
            {
                FfmpegProcess?.Kill();
                FfmpegProcess?.Dispose();
            }
            catch { }
            
            PlaybackCts?.Cancel();
            PlaybackCts?.Dispose();
            PlaybackCts = null;
            Queue.Clear();
        }
    }

    public class SongInfo
    {
        public string Title { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string Author { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public string Thumbnail { get; set; } = "";
        public ulong RequestedBy { get; set; }
    }

    public class MusicCommands : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DiscordSocketClient _client;

        public MusicCommands(DiscordSocketClient client)
        {
            _client = client;
        }

        [SlashCommand("play", "Воспроизвести музыку с YouTube")]
        public async Task PlayCommand(
            [Summary("запрос", "Название песни или ссылка на YouTube")] string query)
        {
            await DeferAsync();

            var user = Context.User as SocketGuildUser;
            if (user?.VoiceChannel == null)
            {
                await FollowupAsync("❌ Вы должны находиться в голосовом канале!", ephemeral: true);
                return;
            }

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
                musicPlayers[Context.Guild.Id] = new MusicPlayer();

            var player = musicPlayers[Context.Guild.Id];
            
            try
            {
                await FollowupAsync($"🔍 Ищу: {query}...");

                // Получаем информацию о видео
                var song = await SearchVideo(query);

                if (song == null)
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = $"❌ Ничего не найдено по запросу: {query}");
                    return;
                }

                if (player.IsPlaying)
                {
                    player.Queue.Enqueue(song);
                    
                    var embed = new EmbedBuilder()
                        .WithTitle("➕ Добавлено в очередь")
                        .WithDescription($"[{song.Title}](https://youtu.be/{song.VideoId})")
                        .WithColor(Color.Blue)
                        .AddField("Автор", song.Author, true)
                        .AddField("Длительность", FormatDuration(song.Duration), true)
                        .AddField("Позиция", player.Queue.Count, true)
                        .WithThumbnailUrl(song.Thumbnail)
                        .Build();

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = "";
                        msg.Embed = embed;
                    });
                }
                else
                {
                    player.VoiceChannel = user.VoiceChannel;
                    player.TextChannel = Context.Channel as ITextChannel;
                    player.CurrentSong = song;
                    
                    await ModifyOriginalResponseAsync(msg => msg.Content = $"🔍 Подключаюсь и начинаю воспроизведение...");
                    
                    // Запускаем воспроизведение
                    _ = Task.Run(async () => await PlaySong(player));
                    
                    var embed = new EmbedBuilder()
                        .WithTitle("🔴 Сейчас играет")
                        .WithDescription($"[{song.Title}](https://youtu.be/{song.VideoId})")
                        .WithColor(Color.Green)
                        .AddField("Автор", song.Author, true)
                        .AddField("Длительность", FormatDuration(song.Duration), true)
                        .AddField("Запросил", user.Mention, true)
                        .WithThumbnailUrl(song.Thumbnail)
                        .Build();

                    await Context.Channel.SendMessageAsync(embed: embed);
                }
            }
            catch (Exception ex)
            {
                await ModifyOriginalResponseAsync(msg => msg.Content = $"❌ Ошибка: {ex.Message}");
            }
        }

        private async Task<SongInfo?> SearchVideo(string query)
        {
            foreach (var instance in InvidiousInstances)
            {
                try
                {
                    // Извлекаем ID видео если это ссылка
                    if (Uri.IsWellFormedUriString(query, UriKind.Absolute))
                    {
                        var videoId = ExtractVideoId(query);
                        if (!string.IsNullOrEmpty(videoId))
                        {
                            var response = await httpClient.GetStringAsync($"{instance}/api/v1/videos/{videoId}");
                            var video = JsonDocument.Parse(response).RootElement;
                            
                            return new SongInfo
                            {
                                Title = video.GetProperty("title").GetString() ?? "Unknown",
                                Author = video.GetProperty("author").GetString() ?? "Unknown",
                                Duration = TimeSpan.FromSeconds(video.GetProperty("lengthSeconds").GetInt32()),
                                Thumbnail = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
                                VideoId = videoId,
                                RequestedBy = Context.User.Id
                            };
                        }
                    }

                    // Поиск по названию
                    var searchResponse = await httpClient.GetStringAsync($"{instance}/api/v1/search?q={Uri.EscapeDataString(query)}");
                    var results = JsonDocument.Parse(searchResponse).RootElement;
                    
                    if (results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        var videoId = first.GetProperty("videoId").GetString();
                        
                        return new SongInfo
                        {
                            Title = first.GetProperty("title").GetString() ?? "Unknown",
                            Author = first.GetProperty("author").GetString() ?? "Unknown",
                            Duration = TimeSpan.FromSeconds(first.GetProperty("lengthSeconds").GetInt32()),
                            Thumbnail = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
                            VideoId = videoId ?? "",
                            RequestedBy = Context.User.Id
                        };
                    }
                }
                catch
                {
                    continue;
                }
            }
            
            return null;
        }

        private string? ExtractVideoId(string url)
        {
            var patterns = new[]
            {
                @"youtube\.com/watch\?v=([^&]+)",
                @"youtu\.be/([^?]+)",
                @"youtube\.com/embed/([^?]+)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(url, pattern);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private async Task PlaySong(MusicPlayer player)
        {
            try
            {
                if (player.CurrentSong == null) return;

                player.IsPlaying = true;
                player.PlaybackCts = new CancellationTokenSource();

                // Подключаемся к голосовому каналу
                if (player.VoiceChannel != null)
                {
                    player.AudioClient = await player.VoiceChannel.ConnectAsync();
                }
                else
                {
                    player.IsPlaying = false;
                    await HandleTrackEnd(player);
                    return;
                }

                // Пробуем разные методы получения аудио
                string? audioUrl = null;
                
                // Метод 1: yt-dlp (самый надежный)
                audioUrl = await GetAudioUrlWithYtDlp(player.CurrentSong.VideoId);
                
                // Метод 2: Invidious (запасной)
                if (string.IsNullOrEmpty(audioUrl))
                {
                    audioUrl = await GetAudioUrlFromInvidious(player.CurrentSong.VideoId);
                }

                if (string.IsNullOrEmpty(audioUrl))
                {
                    await player.TextChannel?.SendMessageAsync("❌ Не удалось получить аудио поток для этого видео");
                    player.IsPlaying = false;
                    await HandleTrackEnd(player);
                    return;
                }

                // Запускаем FFmpeg
                player.FfmpegProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i \"{audioUrl}\" -ac 2 -f s16le -ar 48000 pipe:1",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (player.FfmpegProcess == null)
                {
                    await player.TextChannel?.SendMessageAsync("❌ FFmpeg не установлен");
                    player.IsPlaying = false;
                    await HandleTrackEnd(player);
                    return;
                }

                // Создаем поток для Discord
                if (player.AudioClient != null)
                {
                    var discordStream = player.AudioClient.CreatePCMStream(AudioApplication.Mixed, null, 128 * 1024);
                    
                    try
                    {
                        await player.FfmpegProcess.StandardOutput.BaseStream.CopyToAsync(
                            discordStream, 
                            player.PlaybackCts.Token);
                        await discordStream.FlushAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Playback cancelled");
                    }
                    finally
                    {
                        await discordStream.DisposeAsync();
                    }
                }
                
                player.IsPlaying = false;
                await HandleTrackEnd(player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing: {ex.Message}");
                player.IsPlaying = false;
                await HandleTrackEnd(player);
            }
        }

        private async Task<string?> GetAudioUrlWithYtDlp(string videoId)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"-f bestaudio -g \"https://youtu.be/{videoId}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null) return null;

                var url = await process.StandardOutput.ReadLineAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
                {
                    return url;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> GetAudioUrlFromInvidious(string videoId)
        {
            foreach (var instance in InvidiousInstances)
            {
                try
                {
                    var response = await httpClient.GetStringAsync($"{instance}/api/v1/videos/{videoId}");
                    var video = JsonDocument.Parse(response).RootElement;
                    
                    // Ищем аудио потоки в formatStreams
                    if (video.TryGetProperty("formatStreams", out var formatStreams))
                    {
                        foreach (var stream in formatStreams.EnumerateArray())
                        {
                            var type = stream.GetProperty("type").GetString() ?? "";
                            if (type.Contains("audio/mp4") || type.Contains("audio/webm"))
                            {
                                return stream.GetProperty("url").GetString();
                            }
                        }
                    }

                    // Ищем в adaptiveFormats
                    if (video.TryGetProperty("adaptiveFormats", out var adaptiveFormats))
                    {
                        foreach (var format in adaptiveFormats.EnumerateArray())
                        {
                            var type = format.GetProperty("type").GetString() ?? "";
                            if (type.Contains("audio/mp4") || type.Contains("audio/webm"))
                            {
                                return format.GetProperty("url").GetString();
                            }
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
            
            return null;
        }

        private async Task HandleTrackEnd(MusicPlayer player)
        {
            if (player.Loop && player.CurrentSong != null)
            {
                await PlaySong(player);
            }
            else if (player.Queue.Count > 0)
            {
                player.CurrentSong = player.Queue.Dequeue();
                await PlaySong(player);
                
                var embed = new EmbedBuilder()
                    .WithTitle("🔴 Сейчас играет")
                    .WithDescription($"[{player.CurrentSong.Title}](https://youtu.be/{player.CurrentSong.VideoId})")
                    .WithColor(Color.Green)
                    .AddField("Автор", player.CurrentSong.Author, true)
                    .AddField("Длительность", FormatDuration(player.CurrentSong.Duration), true)
                    .AddField("Запросил", $"<@{player.CurrentSong.RequestedBy}>", true)
                    .WithThumbnailUrl(player.CurrentSong.Thumbnail)
                    .Build();

                if (player.TextChannel != null)
                    await player.TextChannel.SendMessageAsync(embed: embed);
            }
            else
            {
                if (player.TextChannel != null)
                    await player.TextChannel.SendMessageAsync("📢 Очередь закончилась!");
                    
                player.CurrentSong = null;
                
                _ = Task.Delay(60000).ContinueWith(async _ =>
                {
                    if (player.Queue.Count == 0 && !player.IsPlaying)
                    {
                        if (player.VoiceChannel != null)
                            await player.VoiceChannel.DisconnectAsync();
                    }
                });
            }
        }

        [SlashCommand("skip", "Пропустить текущий трек")]
        public async Task SkipCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player?.CurrentSong != null)
            {
                player.PlaybackCts?.Cancel();
                await FollowupAsync($"⏭️ Пропущен: **{player.CurrentSong.Title}**");
            }
            else await FollowupAsync("❌ Сейчас ничего не играет!");
        }

        [SlashCommand("stop", "Остановить воспроизведение")]
        public async Task StopCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player != null)
            {
                player.Stop();
                if (player.VoiceChannel != null)
                    await player.VoiceChannel.DisconnectAsync();
                await FollowupAsync("⏹️ Воспроизведение остановлено");
            }
            else await FollowupAsync("❌ Нет активного воспроизведения!");
        }

        [SlashCommand("queue", "Показать очередь")]
        public async Task QueueCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player == null || (player.Queue.Count == 0 && player.CurrentSong == null))
            {
                await FollowupAsync("📭 Очередь пуста!");
                return;
            }

            var queueList = player.Queue.ToList();
            var description = "";

            for (int i = 0; i < Math.Min(queueList.Count, 10); i++)
            {
                var track = queueList[i];
                description += $"`{i + 1}.` [{Truncate(track.Title, 50)}](https://youtu.be/{track.VideoId}) [{FormatDuration(track.Duration)}]\n";
            }

            if (queueList.Count > 10)
                description += $"\n*... и ещё {queueList.Count - 10} треков*";

            var embed = new EmbedBuilder()
                .WithTitle("📜 Очередь")
                .WithDescription(description)
                .WithColor(Color.Blue)
                .WithFooter($"Всего треков: {queueList.Count}");

            if (player.CurrentSong != null)
            {
                embed.AddField("Сейчас играет", $"[{player.CurrentSong.Title}](https://youtu.be/{player.CurrentSong.VideoId}) [{FormatDuration(player.CurrentSong.Duration)}]");
            }

            await FollowupAsync(embed: embed.Build());
        }

        [SlashCommand("nowplaying", "Что сейчас играет")]
        public async Task NowPlayingCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player?.CurrentSong == null)
            {
                await FollowupAsync("❌ Сейчас ничего не играет!");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("🔴 Сейчас играет")
                .WithDescription($"[{player.CurrentSong.Title}](https://youtu.be/{player.CurrentSong.VideoId})")
                .WithColor(Color.Green)
                .AddField("Автор", player.CurrentSong.Author, true)
                .AddField("Длительность", FormatDuration(player.CurrentSong.Duration), true)
                .AddField("Запросил", $"<@{player.CurrentSong.RequestedBy}>", true)
                .WithThumbnailUrl(player.CurrentSong.Thumbnail)
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("loop", "Включить/выключить повтор")]
        public async Task LoopCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player != null)
            {
                player.Loop = !player.Loop;
                await FollowupAsync(player.Loop ? "🔁 Повтор **включен**" : "➡️ Повтор **выключен**");
            }
            else await FollowupAsync("❌ Нет активного воспроизведения!");
        }

        [SlashCommand("shuffle", "Перемешать очередь")]
        public async Task ShuffleCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player?.Queue.Count < 2)
            {
                await FollowupAsync("❌ Недостаточно треков в очереди!");
                return;
            }

            var list = player!.Queue.ToList();
            var random = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }

            player.Queue = new Queue<SongInfo>(list);
            await FollowupAsync("🔀 Очередь перемешана!");
        }

        [SlashCommand("remove", "Удалить трек из очереди")]
        public async Task RemoveCommand([Summary("номер", "Номер трека")] int number)
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player == null || player.Queue.Count == 0)
            {
                await FollowupAsync("❌ Очередь пуста!");
                return;
            }

            var list = player.Queue.ToList();
            if (number < 1 || number > list.Count)
            {
                await FollowupAsync($"❌ Неверный номер! Всего треков: {list.Count}");
                return;
            }

            var removed = list[number - 1];
            list.RemoveAt(number - 1);
            player.Queue = new Queue<SongInfo>(list);
            await FollowupAsync($"✅ Удален: **{removed.Title}**");
        }

        [SlashCommand("clear", "Очистить очередь")]
        public async Task ClearCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player == null || player.Queue.Count == 0)
            {
                await FollowupAsync("❌ Очередь уже пуста!");
                return;
            }

            var count = player.Queue.Count;
            player.Queue.Clear();
            await FollowupAsync($"🧹 Очередь очищена (удалено {count} треков)");
        }

        [SlashCommand("leave", "Отключить бота")]
        public async Task LeaveCommand()
        {
            await DeferAsync();
            var player = GetPlayer();
            if (player != null)
            {
                player.Stop();
                if (player.VoiceChannel != null)
                    await player.VoiceChannel.DisconnectAsync();
                await FollowupAsync("👋 Отключился");
            }
            else await FollowupAsync("❌ Бот не в голосовом канале!");
        }

        [SlashCommand("help", "Показать команды")]
        public async Task HelpCommand()
        {
            var embed = new EmbedBuilder()
                .WithTitle("🎵 Music Bot - Команды")
                .WithDescription("**Управление музыкой:**")
                .WithColor(Color.Purple)
                .AddField("▶️ **Воспроизведение**", 
                    "`/play` - Найти и играть\n" +
                    "`/nowplaying` - Что играет\n" +
                    "`/queue` - Очередь\n" +
                    "`/skip` - Пропустить\n" +
                    "`/stop` - Остановить", true)
                .AddField("⚙️ **Управление**", 
                    "`/loop` - Повтор\n" +
                    "`/shuffle` - Перемешать\n" +
                    "`/remove` - Удалить из очереди\n" +
                    "`/clear` - Очистить очередь\n" +
                    "`/leave` - Отключить", true)
                .WithFooter("Использует yt-dlp + Invidious")
                .Build();

            await RespondAsync(embed: embed, ephemeral: true);
        }

        private MusicPlayer? GetPlayer()
        {
            return musicPlayers.TryGetValue(Context.Guild.Id, out var player) ? player : null;
        }

        private string Truncate(string str, int maxLength)
        {
            if (str.Length <= maxLength) return str;
            return str[..(maxLength - 3)] + "...";
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.Hours > 0)
            return $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        else
            return $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}
