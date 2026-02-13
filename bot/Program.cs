using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Victoria;
using Victoria.Enums;
using Victoria.EventArgs;
using Victoria.Responses.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

public class Program
{
    private static DiscordSocketClient? client;
    private static InteractionService? interactions;
    private static LavaNode? lavaNode;
    private static IServiceProvider? services;
    private static Dictionary<ulong, Queue<LavaTrack>> musicQueues = new();
    private static Dictionary<ulong, bool> loopEnabled = new();
    private static Dictionary<ulong, int> volumeLevels = new();

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

        // Настраиваем сервисы
        services = new ServiceCollection()
            .AddSingleton<DiscordSocketClient>()
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
            .AddSingleton<LavaConfig>(x => new LavaConfig
            {
                Hostname = "127.0.0.1",
                Port = 2333,
                Authorization = "youshallnotpass",
                SelfDeaf = true,
                EnableResume = true,
                ResumeTimeout = TimeSpan.FromSeconds(30)
            })
            .AddSingleton<LavaNode>()
            .AddLogging(builder => builder.AddConsole())
            .BuildServiceProvider();

        client = services.GetRequiredService<DiscordSocketClient>();
        interactions = services.GetRequiredService<InteractionService>();
        lavaNode = services.GetRequiredService<LavaNode>();

        // Настройка клиента
        client.Log += LogMessage;
        client.Ready += ReadyAsync;
        client.InteractionCreated += InteractionCreatedAsync;
        client.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;

        // Настройка Lavalink
        lavaNode.OnLog += LogMessage;
        lavaNode.OnTrackEnded += TrackEndedAsync;
        lavaNode.OnTrackStarted += TrackStartedAsync;
        lavaNode.OnTrackException += TrackExceptionAsync;
        lavaNode.OnTrackStuck += TrackStuckAsync;

        // Вход в Discord
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
        if (client == null || lavaNode == null) return;
        
        Console.WriteLine($"\n🎉 BOT READY: {client.CurrentUser}");
        Console.WriteLine($"🏰 Servers: {client.Guilds.Count}");

        // Подключаемся к Lavalink
        try
        {
            await lavaNode.ConnectAsync();
            Console.WriteLine("✅ Connected to Lavalink!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to connect to Lavalink: {ex.Message}");
            Console.WriteLine("Make sure Lavalink is running!");
        }

        // Регистрируем команды
        await interactions.AddModuleAsync<MusicCommands>(services);
        await interactions.RegisterCommandsGloballyAsync();
        Console.WriteLine("✅ Slash commands registered globally!");

        foreach (var guild in client.Guilds)
        {
            Console.WriteLine($"   • {guild.Name} (ID: {guild.Id})");
            
            if (!musicQueues.ContainsKey(guild.Id))
                musicQueues[guild.Id] = new Queue<LavaTrack>();
            
            if (!loopEnabled.ContainsKey(guild.Id))
                loopEnabled[guild.Id] = false;
                
            if (!volumeLevels.ContainsKey(guild.Id))
                volumeLevels[guild.Id] = 50;
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
        if (user.IsBot || client == null) return;

        // Если пользователь отключился от голосового канала
        if (oldState.VoiceChannel != null && newState.VoiceChannel == null)
        {
            var guild = (oldState.VoiceChannel as SocketGuildChannel)?.Guild;
            if (guild == null) return;

            // Проверяем, остались ли люди в канале
            var voiceChannel = oldState.VoiceChannel;
            if (voiceChannel.ConnectedUsers.Count == 1 && voiceChannel.ConnectedUsers.Any(x => x.Id == client.CurrentUser.Id))
            {
                // Если остался только бот - отключаемся через 30 секунд
                _ = Task.Delay(30000).ContinueWith(async _ =>
                {
                    var currentChannel = guild.VoiceChannels.FirstOrDefault(x => x.Id == voiceChannel.Id);
                    if (currentChannel != null && currentChannel.ConnectedUsers.Count == 1 && 
                        currentChannel.ConnectedUsers.Any(x => x.Id == client.CurrentUser.Id) && lavaNode != null)
                    {
                        var player = lavaNode.GetPlayer(guild);
                        if (player != null)
                        {
                            await player.StopAsync();
                            await player.TextChannel?.SendMessageAsync("⏰ Отключаюсь из-за отсутствия слушателей!");
                            await lavaNode.LeaveAsync(voiceChannel);
                        }
                    }
                });
            }
        }
    }

    private static async Task TrackEndedAsync(TrackEndedEventArgs args)
    {
        if (args.Reason == TrackEndReason.LoadFailed || args.Reason == TrackEndReason.Cleanup)
            return;

        var guild = args.Player.VoiceChannel.Guild;
        
        if (loopEnabled.ContainsKey(guild.Id) && loopEnabled[guild.Id] && args.Reason != TrackEndReason.Replaced)
        {
            // Повтор текущего трека
            await args.Player.PlayAsync(args.Track);
            return;
        }

        if (musicQueues.ContainsKey(guild.Id) && musicQueues[guild.Id].Count > 0)
        {
            var nextTrack = musicQueues[guild.Id].Dequeue();
            await args.Player.PlayAsync(nextTrack);
            
            var embed = new EmbedBuilder()
                .WithTitle("🎵 Сейчас играет")
                .WithDescription($"[{nextTrack.Title}]({nextTrack.Url})")
                .WithColor(Color.Green)
                .AddField("Автор", nextTrack.Author, true)
                .AddField("Длительность", FormatDuration(nextTrack.Duration), true)
                .AddField("Запросил", $"<@{nextTrack.Context}>", true)
                .WithThumbnailUrl(await nextTrack.FetchArtworkAsync())
                .Build();

            await args.Player.TextChannel?.SendMessageAsync(embed: embed);
        }
        else
        {
            // Очередь пуста
            await args.Player.TextChannel?.SendMessageAsync("📭 Очередь закончилась! Используйте `/play` чтобы добавить новые треки.");
            
            // Отключаемся через минуту если ничего не играет
            _ = Task.Delay(60000).ContinueWith(async _ =>
            {
                if (musicQueues[guild.Id].Count == 0 && args.Player.PlayerState == PlayerState.Stopped)
                {
                    await args.Player.StopAsync();
                    await lavaNode?.LeaveAsync(args.Player.VoiceChannel);
                }
            });
        }
    }

    private static async Task TrackStartedAsync(TrackStartedEventArgs args)
    {
        Console.WriteLine($"🎵 Now playing: {args.Track.Title} in {args.Player.VoiceChannel.Guild.Name}");
    }

    private static async Task TrackExceptionAsync(TrackExceptionEventArgs args)
    {
        Console.WriteLine($"❌ Track exception: {args.Exception.Message}");
        await args.Player.TextChannel?.SendMessageAsync($"❌ Ошибка воспроизведения: {args.Exception.Message}");
    }

    private static async Task TrackStuckAsync(TrackStuckEventArgs args)
    {
        Console.WriteLine($"❌ Track stuck: {args.Track.Title}");
        await args.Player.TextChannel?.SendMessageAsync($"❌ Трек завис, пропускаю...");
        
        // Пропускаем зависший трек
        if (musicQueues[args.Player.VoiceChannel.Guild.Id].Count > 0)
        {
            var nextTrack = musicQueues[args.Player.VoiceChannel.Guild.Id].Dequeue();
            await args.Player.PlayAsync(nextTrack);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.Hours > 0)
            return $"{duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        else
            return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    // === МУЗЫКАЛЬНЫЕ КОМАНДЫ ===
    public class MusicCommands : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly LavaNode _lavaNode;
        private readonly DiscordSocketClient _client;

        public MusicCommands(LavaNode lavaNode, DiscordSocketClient client)
        {
            _lavaNode = lavaNode;
            _client = client;
        }

        [SlashCommand("play", "Воспроизвести музыку (по названию или ссылке)")]
        public async Task PlayCommand(
            [Summary("запрос", "Название песни или ссылка на YouTube/Spotify")] string query)
        {
            await DeferAsync();

            var user = Context.User as SocketGuildUser;
            if (user == null || user.VoiceChannel == null)
            {
                await FollowupAsync("❌ Вы должны находиться в голосовом канале!", ephemeral: true);
                return;
            }

            // Подключаемся к голосовому каналу
            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                try
                {
                    await _lavaNode.JoinAsync(user.VoiceChannel, Context.Channel as ITextChannel);
                }
                catch (Exception ex)
                {
                    await FollowupAsync($"❌ Не удалось подключиться: {ex.Message}");
                    return;
                }
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            if (player == null)
            {
                await FollowupAsync("❌ Не удалось получить плеер!");
                return;
            }

            // Поиск трека
            SearchResponse searchResponse;
            if (Uri.IsWellFormedUriString(query, UriKind.Absolute))
            {
                searchResponse = await _lavaNode.SearchAsync(SearchType.Direct, query);
            }
            else
            {
                searchResponse = await _lavaNode.SearchYouTubeAsync(query);
            }

            if (searchResponse.Status == SearchStatus.NoMatches)
            {
                await FollowupAsync($"❌ Ничего не найдено по запросу: {query}");
                return;
            }

            if (searchResponse.Status == SearchStatus.LoadFailed)
            {
                await FollowupAsync($"❌ Не удалось загрузить трек: {searchResponse.Exception?.Message}");
                return;
            }

            // Обработка результатов
            var tracks = searchResponse.Tracks.ToList();
            var track = tracks.First();

            // Добавляем информацию о запросившем
            track.Context = user.Id;

            if (player.PlayerState == PlayerState.Playing || player.PlayerState == PlayerState.Paused)
            {
                // Добавляем в очередь
                if (!musicQueues.ContainsKey(Context.Guild.Id))
                    musicQueues[Context.Guild.Id] = new Queue<LavaTrack>();

                musicQueues[Context.Guild.Id].Enqueue(track);

                var embed = new EmbedBuilder()
                    .WithTitle("➕ Добавлено в очередь")
                    .WithDescription($"[{track.Title}]({track.Url})")
                    .WithColor(Color.Blue)
                    .AddField("Автор", track.Author, true)
                    .AddField("Длительность", FormatDuration(track.Duration), true)
                    .AddField("Позиция", musicQueues[Context.Guild.Id].Count, true)
                    .WithThumbnailUrl(await track.FetchArtworkAsync())
                    .Build();

                await FollowupAsync(embed: embed);
            }
            else
            {
                // Играем сразу
                await player.PlayAsync(track);
                
                var embed = new EmbedBuilder()
                    .WithTitle("🎵 Сейчас играет")
                    .WithDescription($"[{track.Title}]({track.Url})")
                    .WithColor(Color.Green)
                    .AddField("Автор", track.Author, true)
                    .AddField("Длительность", FormatDuration(track.Duration), true)
                    .AddField("Запросил", user.Mention, true)
                    .WithThumbnailUrl(await track.FetchArtworkAsync())
                    .Build();

                await FollowupAsync(embed: embed);
            }
        }

        [SlashCommand("search", "Поиск и выбор из нескольких результатов")]
        public async Task SearchCommand(
            [Summary("запрос", "Название для поиска")] string query)
        {
            await DeferAsync();

            var user = Context.User as SocketGuildUser;
            if (user == null || user.VoiceChannel == null)
            {
                await FollowupAsync("❌ Вы должны находиться в голосовом канале!", ephemeral: true);
                return;
            }

            var searchResponse = await _lavaNode.SearchYouTubeAsync(query);
            
            if (searchResponse.Status == SearchStatus.NoMatches)
            {
                await FollowupAsync($"❌ Ничего не найдено по запросу: {query}");
                return;
            }

            var tracks = searchResponse.Tracks.Take(5).ToList();
            var selectMenu = new SelectMenuBuilder()
                .WithPlaceholder("Выберите трек")
                .WithCustomId("track_select")
                .WithMinValues(1)
                .WithMaxValues(1);

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                selectMenu.AddOption(
                    $"{i + 1}. {Truncate(track.Title, 50)}",
                    track.Url,
                    $"{track.Author} • {FormatDuration(track.Duration)}"
                );
            }

            var component = new ComponentBuilder()
                .WithSelectMenu(selectMenu)
                .Build();

            await FollowupAsync("🔍 **Результаты поиска:**", components: component);
            
            // Сохраняем результаты для последующего выбора
            var searchResults = new Dictionary<string, LavaTrack>();
            foreach (var track in tracks)
            {
                searchResults[track.Url] = track;
            }
            
            // Обработка выбора (нужно добавить InteractionCreated handler для select menu)
        }

        [SlashCommand("skip", "Пропустить текущий трек")]
        public async Task SkipCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            var currentTrack = player.Track;

            if (musicQueues.ContainsKey(Context.Guild.Id) && musicQueues[Context.Guild.Id].Count > 0)
            {
                var nextTrack = musicQueues[Context.Guild.Id].Dequeue();
                await player.PlayAsync(nextTrack);
                
                await FollowupAsync($"⏭️ Пропущен: **{currentTrack.Title}**\n🎵 Сейчас играет: **{nextTrack.Title}**");
            }
            else
            {
                await player.StopAsync();
                await FollowupAsync($"⏹️ Остановлено: **{currentTrack.Title}**");
            }
        }

        [SlashCommand("stop", "Остановить воспроизведение и очистить очередь")]
        public async Task StopCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            await player.StopAsync();
            
            if (musicQueues.ContainsKey(Context.Guild.Id))
                musicQueues[Context.Guild.Id].Clear();

            await FollowupAsync("⏹️ Воспроизведение остановлено, очередь очищена");
        }

        [SlashCommand("pause", "Поставить на паузу")]
        public async Task PauseCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            
            if (player.PlayerState == PlayerState.Paused)
            {
                await FollowupAsync("⏸️ Уже на паузе!");
                return;
            }

            await player.PauseAsync();
            await FollowupAsync("⏸️ Воспроизведение приостановлено");
        }

        [SlashCommand("resume", "Возобновить воспроизведение")]
        public async Task ResumeCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            
            if (player.PlayerState != PlayerState.Paused)
            {
                await FollowupAsync("▶️ Уже играет!");
                return;
            }

            await player.ResumeAsync();
            await FollowupAsync("▶️ Воспроизведение возобновлено");
        }

        [SlashCommand("queue", "Показать текущую очередь")]
        public async Task QueueCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            
            if (!musicQueues.ContainsKey(Context.Guild.Id) || musicQueues[Context.Guild.Id].Count == 0)
            {
                await FollowupAsync("📭 Очередь пуста!");
                return;
            }

            var queueList = musicQueues[Context.Guild.Id].ToList();
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
                .WithTitle("📜 Очередь воспроизведения")
                .WithDescription(description)
                .WithColor(Color.Blue)
                .AddField("Сейчас играет", $"[{player.Track.Title}]({player.Track.Url}) [{FormatDuration(player.Track.Duration)}]")
                .WithFooter($"Всего треков: {queueList.Count}")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("nowplaying", "Что сейчас играет")]
        public async Task NowPlayingCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            var track = player.Track;
            var position = player.PlaybackPosition;

            var progress = CreateProgressBar(position, track.Duration);

            var embed = new EmbedBuilder()
                .WithTitle("🎵 Сейчас играет")
                .WithDescription($"[{track.Title}]({track.Url})")
                .WithColor(Color.Green)
                .AddField("Автор", track.Author, true)
                .AddField("Длительность", $"{FormatDuration(position)} / {FormatDuration(track.Duration)}", true)
                .AddField("Запросил", $"<@{track.Context}>", true)
                .AddField("Прогресс", progress, false)
                .WithThumbnailUrl(await track.FetchArtworkAsync())
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("loop", "Включить/выключить повтор трека")]
        public async Task LoopCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            if (!loopEnabled.ContainsKey(Context.Guild.Id))
                loopEnabled[Context.Guild.Id] = false;

            loopEnabled[Context.Guild.Id] = !loopEnabled[Context.Guild.Id];

            if (loopEnabled[Context.Guild.Id])
            {
                await FollowupAsync("🔁 Повтор трека **включен**");
            }
            else
            {
                await FollowupAsync("➡️ Повтор трека **выключен**");
            }
        }

        [SlashCommand("shuffle", "Перемешать очередь")]
        public async Task ShuffleCommand()
        {
            await DeferAsync();

            if (!musicQueues.ContainsKey(Context.Guild.Id) || musicQueues[Context.Guild.Id].Count < 2)
            {
                await FollowupAsync("❌ Недостаточно треков в очереди для перемешивания!");
                return;
            }

            var list = musicQueues[Context.Guild.Id].ToList();
            var random = new Random();
            
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }

            musicQueues[Context.Guild.Id] = new Queue<LavaTrack>(list);
            await FollowupAsync("🔀 Очередь перемешана!");
        }

        [SlashCommand("remove", "Удалить трек из очереди")]
        public async Task RemoveCommand(
            [Summary("номер", "Номер трека в очереди")] int number)
        {
            await DeferAsync();

            if (!musicQueues.ContainsKey(Context.Guild.Id) || musicQueues[Context.Guild.Id].Count == 0)
            {
                await FollowupAsync("❌ Очередь пуста!");
                return;
            }

            if (number < 1 || number > musicQueues[Context.Guild.Id].Count)
            {
                await FollowupAsync($"❌ Неверный номер! Всего треков: {musicQueues[Context.Guild.Id].Count}");
                return;
            }

            var list = musicQueues[Context.Guild.Id].ToList();
            var removed = list[number - 1];
            list.RemoveAt(number - 1);
            musicQueues[Context.Guild.Id] = new Queue<LavaTrack>(list);

            await FollowupAsync($"✅ Удален трек #{number}: **{removed.Title}**");
        }

        [SlashCommand("clear", "Очистить всю очередь")]
        public async Task ClearQueueCommand()
        {
            await DeferAsync();

            if (!musicQueues.ContainsKey(Context.Guild.Id) || musicQueues[Context.Guild.Id].Count == 0)
            {
                await FollowupAsync("❌ Очередь уже пуста!");
                return;
            }

            var count = musicQueues[Context.Guild.Id].Count;
            musicQueues[Context.Guild.Id].Clear();

            await FollowupAsync($"🧹 Очередь очищена (удалено {count} треков)");
        }

        [SlashCommand("volume", "Изменить громкость")]
        public async Task VolumeCommand(
            [Summary("уровень", "Громкость от 0 до 100")] int volume)
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            if (volume < 0 || volume > 100)
            {
                await FollowupAsync("❌ Громкость должна быть от 0 до 100!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            await player.UpdateVolumeAsync((ushort)volume);
            
            volumeLevels[Context.Guild.Id] = volume;
            
            await FollowupAsync($"🔊 Громкость установлена на {volume}%");
        }

        [SlashCommand("seek", "Перемотать на указанное время")]
        public async Task SeekCommand(
            [Summary("время", "Время в формате мм:сс (например 1:30)")] string time)
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            if (!TimeSpan.TryParse($"00:{time}", out var seekTime))
            {
                await FollowupAsync("❌ Неверный формат времени! Используйте мм:сс (например 1:30)");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            
            if (seekTime > player.Track.Duration)
            {
                await FollowupAsync($"❌ Время не может превышать длительность трека ({FormatDuration(player.Track.Duration)})!");
                return;
            }

            await player.SeekAsync(seekTime);
            await FollowupAsync($"⏩ Перемотано на {FormatDuration(seekTime)}");
        }

        [SlashCommand("leave", "Отключить бота от голосового канала")]
        public async Task LeaveCommand()
        {
            await DeferAsync();

            if (!_lavaNode.HasPlayer(Context.Guild))
            {
                await FollowupAsync("❌ Бот не находится в голосовом канале!");
                return;
            }

            var player = _lavaNode.GetPlayer(Context.Guild);
            await player.StopAsync();
            
            if (musicQueues.ContainsKey(Context.Guild.Id))
                musicQueues[Context.Guild.Id].Clear();
                
            await _lavaNode.LeaveAsync(player.VoiceChannel);
            
            await FollowupAsync("👋 Отключился от голосового канала");
        }

        [SlashCommand("help", "Показать список музыкальных команд")]
        public async Task HelpCommand()
        {
            var embed = new EmbedBuilder()
                .WithTitle("🎵 Music Bot - Все команды")
                .WithDescription("**Управление музыкой через слеш-команды:**")
                .WithColor(Color.Purple)
                .AddField("▶️ **Воспроизведение**", 
                    "`/play` - Найти и играть трек\n" +
                    "`/search` - Поиск с выбором\n" +
                    "`/nowplaying` - Что сейчас играет\n" +
                    "`/queue` - Показать очередь\n" +
                    "`/loop` - Повтор трека\n" +
                    "`/shuffle` - Перемешать очередь\n" +
                    "`/clear` - Очистить очередь\n" +
                    "`/remove` - Удалить из очереди", true)
                .AddField("⏯️ **Управление**", 
                    "`/pause` - Пауза\n" +
                    "`/resume` - Продолжить\n" +
                    "`/skip` - Пропустить\n" +
                    "`/stop` - Остановить\n" +
                    "`/seek` - Перемотка\n" +
                    "`/volume` - Громкость\n" +
                    "`/leave` - Отключиться", true)
                .AddField("📋 **Форматы**",
                    "• Название песни\n" +
                    "• YouTube ссылка\n" +
                    "• Spotify ссылка\n" +
                    "• SoundCloud ссылка", false)
                .WithFooter($"Серверов: {_client.Guilds.Count} • Хостинг: GitHub Actions")
                .WithCurrentTimestamp()
                .Build();

            await RespondAsync(embed: embed, ephemeral: true);
        }

        private string Truncate(string str, int maxLength)
        {
            if (str.Length <= maxLength) return str;
            return str.Substring(0, maxLength - 3) + "...";
        }

        private string CreateProgressBar(TimeSpan current, TimeSpan total)
        {
            int totalBars = 20;
            double progress = current.TotalSeconds / total.TotalSeconds;
            int filledBars = (int)Math.Round(progress * totalBars);
            
            string bar = "";
            for (int i = 0; i < totalBars; i++)
            {
                if (i == filledBars)
                    bar += "🔘";
                else if (i < filledBars)
                    bar += "▰";
                else
                    bar += "▱";
            }
            
            return bar;
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.Hours > 0)
                return $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            else
                return $"{duration.Minutes}:{duration.Seconds:D2}";
        }
    }
}
