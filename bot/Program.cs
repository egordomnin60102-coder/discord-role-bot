using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using System.Diagnostics;
using System.Collections.Concurrent;

public class Program
{
    private static DiscordSocketClient? client;
    private static InteractionService? interactions;
    private static IServiceProvider? services;
    private static YoutubeClient youtube = new();
    
    // Хранилище для очередей и состояний
    private static ConcurrentDictionary<ulong, MusicPlayer> musicPlayers = new();

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

        // Проверяем, остались ли люди в канале
        if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
        {
            var guild = (oldState.VoiceChannel as SocketGuildChannel)?.Guild;
            if (guild == null) return;

            var voiceChannel = oldState.VoiceChannel;
            if (voiceChannel.ConnectedUsers.Count == 1 && voiceChannel.ConnectedUsers.Any(x => x.Id == client?.CurrentUser.Id))
            {
                _ = Task.Delay(30000).ContinueWith(async _ =>
                {
                    var currentChannel = guild.VoiceChannels.FirstOrDefault(x => x.Id == voiceChannel.Id);
                    if (currentChannel != null && currentChannel.ConnectedUsers.Count == 1 && 
                        currentChannel.ConnectedUsers.Any(x => x.Id == client?.CurrentUser.Id))
                    {
                        await currentChannel.DisconnectAsync();
                        if (musicPlayers.ContainsKey(guild.Id))
                        {
                            musicPlayers[guild.Id].Stop();
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
        public bool IsPaused { get; set; } = false;
        public bool Loop { get; set; } = false;
        public int Volume { get; set; } = 50;
        public IVoiceChannel? VoiceChannel { get; set; }
        public ITextChannel? TextChannel { get; set; }
        public Process? FfmpegProcess { get; set; }
        public IAudioClient? AudioClient { get; set; }
        public SongInfo? CurrentSong { get; set; }
        public CancellationTokenSource? PlaybackCts { get; set; }

        public void Stop()
        {
            IsPlaying = false;
            IsPaused = false;
            CurrentSong = null;
            FfmpegProcess?.Kill();
            FfmpegProcess?.Dispose();
            FfmpegProcess = null;
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
        private static YoutubeClient youtube = new();

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
                // Поиск видео
                VideoSearchResult video;
                if (Uri.IsWellFormedUriString(query, UriKind.Absolute))
                {
                    var v = await youtube.Videos.GetAsync(query);
                    video = new VideoSearchResult
                    {
                        Title = v.Title,
                        Url = v.Url,
                        Author = v.Author.ChannelTitle,
                        Duration = v.Duration ?? TimeSpan.Zero,
                        Thumbnail = v.Thumbnails.FirstOrDefault()?.Url ?? ""
                    };
                }
                else
                {
                    var results = await youtube.Search.GetVideosAsync(query);
                    var first = results.FirstOrDefault();
                    if (first == null)
                    {
                        await FollowupAsync($"❌ Ничего не найдено по запросу: {query}");
                        return;
                    }
                    
                    var v = await youtube.Videos.GetAsync(first.Url);
                    video = new VideoSearchResult
                    {
                        Title = v.Title,
                        Url = v.Url,
                        Author = v.Author.ChannelTitle,
                        Duration = v.Duration ?? TimeSpan.Zero,
                        Thumbnail = v.Thumbnails.FirstOrDefault()?.Url ?? ""
                    };
                }

                var song = new SongInfo
                {
                    Title = video.Title,
                    Url = video.Url,
                    Author = video.Author,
                    Duration = video.Duration,
                    Thumbnail = video.Thumbnail,
                    RequestedBy = user.Id
                };

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

                    await FollowupAsync(embed: embed);
                }
                else
                {
                    player.VoiceChannel = user.VoiceChannel;
                    player.TextChannel = Context.Channel as ITextChannel;
                    player.CurrentSong = song;
                    
                    await FollowupAsync($"🔍 Подключаюсь и начинаю воспроизведение...");
                    
                    await PlaySong(player, song);
                    
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
                await FollowupAsync($"❌ Ошибка: {ex.Message}");
            }
        }

        private async Task PlaySong(MusicPlayer player, SongInfo song)
        {
            try
            {
                player.IsPlaying = true;
                player.PlaybackCts = new CancellationTokenSource();

                // Получаем аудио поток
                var streamManifest = await youtube.Videos.Streams.GetManifestAsync(song.Url);
                var audioStream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                if (audioStream == null)
                {
                    await player.TextChannel?.SendMessageAsync("❌ Не удалось получить аудио поток");
                    player.IsPlaying = false;
                    return;
                }

                // Подключаемся к голосовому каналу
                player.AudioClient = await player.VoiceChannel?.ConnectAsync();
                
                // Создаем FFmpeg процесс для конвертации
                var ffmpeg = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i pipe:0 -ac 2 -f s16le -ar 48000 pipe:1",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (ffmpeg == null)
                {
                    await player.TextChannel?.SendMessageAsync("❌ FFmpeg не установлен");
                    player.IsPlaying = false;
                    return;
                }

                player.FfmpegProcess = ffmpeg;

                // Скачиваем и передаем в FFmpeg
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var audio = await youtube.Videos.Streams.GetAsync(audioStream);
                        await audio.CopyToAsync(ffmpeg.StandardInput.BaseStream);
                        ffmpeg.StandardInput.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error downloading: {ex.Message}");
                    }
                });

                // Создаем поток для Discord
                var discordStream = player.AudioClient.CreatePCMStream(AudioApplication.Mixed, null, 128 * 1024);
                
                // Передаем аудио в Discord
                await ffmpeg.StandardOutput.BaseStream.CopyToAsync(discordStream, player.PlaybackCts.Token);
                await discordStream.FlushAsync();
                player.IsPlaying = false;

                // После окончания трека
                await HandleTrackEnd(player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing: {ex.Message}");
                player.IsPlaying = false;
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

                await player.TextChannel?.SendMessageAsync(embed: embed);
            }
            else
            {
                await player.TextChannel?.SendMessageAsync("📭 Очередь закончилась!");
                player.CurrentSong = null;
                
                // Отключаемся через минуту
                _ = Task.Delay(60000).ContinueWith(async _ =>
                {
                    if (player.Queue.Count == 0 && !player.IsPlaying)
                    {
                        await player.VoiceChannel?.DisconnectAsync();
                    }
                });
            }
        }

        [SlashCommand("skip", "Пропустить текущий трек")]
        public async Task SkipCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Нет активного воспроизведения!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            if (!player.IsPlaying || player.CurrentSong == null)
            {
                await FollowupAsync("❌ Сейчас ничего не играет!");
                return;
            }

            var skipped = player.CurrentSong;
            player.PlaybackCts?.Cancel();

            await FollowupAsync($"⏭️ Пропущен: **{skipped.Title}**");
        }

        [SlashCommand("stop", "Остановить воспроизведение")]
        public async Task StopCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Нет активного воспроизведения!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            player.Stop();
            await player.VoiceChannel?.DisconnectAsync();

            await FollowupAsync("⏹️ Воспроизведение остановлено");
        }

        [SlashCommand("pause", "Поставить на паузу")]
        public async Task PauseCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Нет активного воспроизведения!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            // В этой упрощенной версии пауза не поддерживается
            await FollowupAsync("⏸️ Функция паузы временно недоступна");
        }

        [SlashCommand("resume", "Продолжить воспроизведение")]
        public async Task ResumeCommand()
        {
            await DeferAsync();
            await FollowupAsync("▶️ Функция продолжения временно недоступна");
        }

        [SlashCommand("queue", "Показать очередь")]
        public async Task QueueCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("📭 Очередь пуста!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            var queueList = player.Queue.ToList();

            if (queueList.Count == 0 && player.CurrentSong == null)
            {
                await FollowupAsync("📭 Очередь пуста!");
                return;
            }

            var description = "";
            for (int i = 0; i < Math.Min(queueList.Count, 10); i++)
            {
                var track = queueList[i];
                description += $"`{i + 1}.` [{Truncate(track.Title, 50)}]({track.Url}) [{FormatDuration(track.Duration)}]\n";
            }

            if (queueList.Count > 10)
            {
                description += $"\n*... и ещё {queueList.Count - 10} треков*";
            }

            var embed = new EmbedBuilder()
                .WithTitle("📜 Очередь")
                .WithDescription(description)
                .WithColor(Color.Blue)
                .WithFooter($"Всего треков: {queueList.Count}")
                .Build();

            if (player.CurrentSong != null)
            {
                embed.AddField("Сейчас играет", $"[{player.CurrentSong.Title}]({player.CurrentSong.Url}) [{FormatDuration(player.CurrentSong.Duration)}]");
            }

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("nowplaying", "Что сейчас играет")]
        public async Task NowPlayingCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Сейчас ничего не играет!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            if (player.CurrentSong == null)
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

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Нет активного воспроизведения!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            player.Loop = !player.Loop;

            await FollowupAsync(player.Loop ? "🔁 Повтор **включен**" : "➡️ Повтор **выключен**");
        }

        [SlashCommand("shuffle", "Перемешать очередь")]
        public async Task ShuffleCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Нет активного воспроизведения!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            var list = player.Queue.ToList();
            
            if (list.Count < 2)
            {
                await FollowupAsync("❌ Недостаточно треков в очереди!");
                return;
            }

            var random = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }

            player.Queue = new Queue<SongInfo>(list);
            await FollowupAsync("🔀 Очередь перемешана!");
        }

        [SlashCommand("remove", "Удалить трек из очереди")]
        public async Task RemoveCommand(
            [Summary("номер", "Номер трека")] int number)
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Очередь пуста!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
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

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Очередь уже пуста!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            var count = player.Queue.Count;
            player.Queue.Clear();

            await FollowupAsync($"🧹 Очередь очищена (удалено {count} треков)");
        }

        [SlashCommand("leave", "Отключить бота")]
        public async Task LeaveCommand()
        {
            await DeferAsync();

            if (!musicPlayers.ContainsKey(Context.Guild.Id))
            {
                await FollowupAsync("❌ Бот не в голосовом канале!");
                return;
            }

            var player = musicPlayers[Context.Guild.Id];
            player.Stop();
            await player.VoiceChannel?.DisconnectAsync();

            await FollowupAsync("👋 Отключился");
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
                .WithFooter("Требуется FFmpeg")
                .Build();

            await RespondAsync(embed: embed, ephemeral: true);
        }

        private string Truncate(string str, int maxLength)
        {
            if (str.Length <= maxLength) return str;
            return str[..(maxLength - 3)] + "...";
        }
    }

    public class VideoSearchResult
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Author { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public string Thumbnail { get; set; } = "";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.Hours > 0)
            return $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        else
            return $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}
