using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;

public class Program
{
    private static DiscordSocketClient? client;
    private static InteractionService? interactions;
    private static IServiceProvider? services;
    private static ConcurrentDictionary<ulong, MusicPlayer> musicPlayers = new();
    private static HttpClient httpClient = new();

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

        // Проверяем наличие yt-dlp
        try
        {
            var check = Process.Start(new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            await check.WaitForExitAsync();
            Console.WriteLine($"✅ yt-dlp version: {(await check.StandardOutput.ReadToEndAsync()).Trim()}");
        }
        catch
        {
            Console.WriteLine("❌ yt-dlp not found! Installing...");
            Process.Start("pip", "install yt-dlp").WaitForExit();
        }

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
        public Process? YtDlpProcess { get; set; }
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
            
            try
            {
                YtDlpProcess?.Kill();
                YtDlpProcess?.Dispose();
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
        public string Url { get; set; } = "";
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

                // Получаем информацию о видео через yt-dlp
                var song = await GetVideoInfo(query);

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
                        .WithDescription($"[{song.Title}]({song.Url})")
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
                    _ = Task.Run(async () => await PlaySong(player, song));
                    
                    var embed = new EmbedBuilder()
                        .WithTitle("🎵 Сейчас играет")
                        .WithDescription($"[{song.Title}]({song.Url})")
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

        private async Task<SongInfo?> GetVideoInfo(string query)
        {
            try
            {
                // Если это не ссылка, добавляем для поиска
                if (!Uri.IsWellFormedUriString(query, UriKind.Absolute))
                {
                    query = $"ytsearch1:{query}";
                }

                // Получаем информацию через yt-dlp
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--dump-json --no-playlist \"{query}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null) return null;

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrEmpty(output)) return null;

                var json = JsonDocument.Parse(output).RootElement;
                
                // Парсим длительность
                var durationStr = json.GetProperty("duration").GetInt32();
                var duration = TimeSpan.FromSeconds(durationStr);

                return new SongInfo
                {
                    Title = json.GetProperty("title").GetString() ?? "Unknown",
                    Url = json.GetProperty("webpage_url").GetString() ?? query,
                    Author = json.GetProperty("uploader").GetString() ?? "Unknown",
                    Duration = duration,
                    Thumbnail = json.GetProperty("thumbnail").GetString() ?? "",
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting video info: {ex.Message}");
                return null;
            }
        }

        private async Task PlaySong(MusicPlayer player, SongInfo song)
        {
            try
            {
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
                    return;
                }

                // Запускаем yt-dlp для получения аудио потока
                player.YtDlpProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"-f bestaudio -o - \"{song.Url}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (player.YtDlpProcess == null)
                {
                    await player.TextChannel?.SendMessageAsync("❌ Не удалось запустить yt-dlp");
                    player.IsPlaying = false;
                    return;
                }

                // Запускаем FFmpeg для конвертации
                player.FfmpegProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (player.FfmpegProcess == null)
                {
                    await player.TextChannel?.SendMessageAsync("❌ FFmpeg не установлен");
                    player.IsPlaying = false;
                    return;
                }

                // Передаем поток из yt-dlp в FFmpeg
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await player.YtDlpProcess.StandardOutput.BaseStream.CopyToAsync(
                            player.FfmpegProcess.StandardInput.BaseStream, 
                            player.PlaybackCts.Token);
                        player.FfmpegProcess.StandardInput.Close();
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Streaming cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error streaming: {ex.Message}");
                    }
                });

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
                }
                
                player.IsPlaying = false;

                // После окончания трека
                await HandleTrackEnd(player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing: {ex.Message}");
                player.IsPlaying = false;
                await HandleTrackEnd(player);
            }
        }

        private async Task HandleTrackEnd(MusicPlayer player)
        {
            if (player.Loop && player.CurrentSong != null)
            {
                await PlaySong(player, player.CurrentSong);
            }
            else if (player.Queue.Count > 0)
            {
                player.CurrentSong = player.Queue.Dequeue();
                await PlaySong(player, player.CurrentSong);
                
                var embed = new EmbedBuilder()
                    .WithTitle("🎵 Сейчас играет")
                    .WithDescription($"[{player.CurrentSong.Title}]({player.CurrentSong.Url})")
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
                    await player.TextChannel.SendMessageAsync("📭 Очередь закончилась!");
                    
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
                description += $"`{i + 1}.` [{Truncate(track.Title, 50)}]({track.Url}) [{FormatDuration(track.Duration)}]\n";
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
                embed.AddField("Сейчас играет", $"[{player.CurrentSong.Title}]({player.CurrentSong.Url}) [{FormatDuration(player.CurrentSong.Duration)}]");
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
                .WithTitle("🎵 Сейчас играет")
                .WithDescription($"[{player.CurrentSong.Title}]({player.CurrentSong.Url})")
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
                .WithFooter("Использует yt-dlp")
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
