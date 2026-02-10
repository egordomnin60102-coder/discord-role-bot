using Discord;
using Discord.Interactions;
using System.Net.Http;
using System.Text.Json;

[Group("fun", "🎉 Развлечения")]
public class FunModule : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly HttpClient _httpClient = new();
    private static readonly Random _random = new();
    
    [SlashCommand("meme", "Получить случайный мем")]
    public async Task GetMeme()
    {
        await DeferAsync();
        
        try
        {
            var response = await _httpClient.GetStringAsync("https://meme-api.com/gimme");
            var meme = JsonSerializer.Deserialize<MemeResponse>(response);
            
            var embed = new EmbedBuilder()
                .WithTitle(meme.title)
                .WithImageUrl(meme.url)
                .WithColor(Color.Gold)
                .WithFooter($"👁️ {meme.ups} | 📂 r/{meme.subreddit}")
                .Build();
            
            await FollowupAsync(embed: embed);
        }
        catch
        {
            await FollowupAsync("❌ Не удалось получить мем, попробуйте позже");
        }
    }
    
    [SlashCommand("cat", "Получить случайного котика")]
    public async Task GetCat()
    {
        await DeferAsync();
        
        try
        {
            var response = await _httpClient.GetStringAsync("https://api.thecatapi.com/v1/images/search");
            var cats = JsonSerializer.Deserialize<CatResponse[]>(response);
            var cat = cats.First();
            
            var embed = new EmbedBuilder()
                .WithTitle("🐱 Случайный котик")
                .WithImageUrl(cat.url)
                .WithColor(Color.LightGrey)
                .WithFooter("Источник: The Cat API")
                .Build();
            
            await FollowupAsync(embed: embed);
        }
        catch
        {
            await FollowupAsync("❌ Не удалось получить котика, попробуйте позже");
        }
    }
    
    [SlashCommand("8ball", "Задать вопрос волшебному шару")]
    public async Task MagicBall(
        [Summary("вопрос", "Ваш вопрос шару")] string question)
    {
        var answers = new[]
        {
            "Бесспорно ✅", "Предрешено ✅", "Никаких сомнений ✅", "Определённо да ✅", "Можешь быть уверен в этом ✅",
            "Мне кажется — да ✅", "Вероятнее всего ✅", "Хорошие перспективы ✅", "Знаки говорят — да ✅", "Да ✅",
            "Пока не ясно, попробуй снова 🔄", "Спроси позже 🔄", "Лучше не рассказывать 🔄", "Сейчас нельзя предсказать 🔄", "Сконцентрируйся и спроси опять 🔄",
            "Даже не думай ❌", "Мой ответ — нет ❌", "По моим данным — нет ❌", "Перспективы не очень хорошие ❌", "Весьма сомнительно ❌"
        };
        
        var answer = answers[_random.Next(answers.Length)];
        
        var embed = new EmbedBuilder()
            .WithTitle("🎱 Волшебный шар")
            .AddField("❓ Вопрос", question)
            .AddField("🎱 Ответ", answer)
            .WithColor(answer.Contains("✅") ? Color.Green : answer.Contains("❌") ? Color.Red : Color.Gold)
            .WithThumbnailUrl("https://cdn.discordapp.com/emojis/1013800461386387466.png")
            .WithFooter($"Запросил: {Context.User.Username}")
            .Build();
        
        await RespondAsync(embed: embed);
    }
    
    [SlashCommand("coin", "Подбросить монетку")]
    public async Task FlipCoin()
    {
        var result = _random.Next(2) == 0 ? "Орёл 🦅" : "Решка 🪙";
        
        var embed = new EmbedBuilder()
            .WithTitle("🪙 Подбрасываем монетку...")
            .WithDescription($"**Результат: {result}**")
            .WithColor(Color.Gold)
            .Build();
        
        await RespondAsync(embed: embed);
    }
    
    [SlashCommand("dice", "Бросить кубик")]
    public async Task RollDice(
        [Summary("кости", "Сколько костей бросать")] [MinValue(1)] [MaxValue(5)] int dice = 1,
        [Summary("стороны", "Сколько сторон у кубика")] [MinValue(4)] [MaxValue(100)] int sides = 6)
    {
        await DeferAsync();
        
        var results = new List<int>();
        var total = 0;
        
        for (int i = 0; i < dice; i++)
        {
            var roll = _random.Next(1, sides + 1);
            results.Add(roll);
            total += roll;
        }
        
        var embed = new EmbedBuilder()
            .WithTitle($"🎲 Бросок {dice}к{sides}")
            .WithDescription($"**Результаты:** {string.Join(", ", results)}\n**Сумма:** {total}")
            .WithColor(Color.Green)
            .WithFooter($"Запросил: {Context.User.Username}")
            .Build();
        
        if (dice == 2 && sides == 6)
        {
            var diceEmojis = new Dictionary<int, string>
            {
                {1, "⚀"}, {2, "⚁"}, {3, "⚂"}, {4, "⚃"}, {5, "⚄"}, {6, "⚅"}
            };
            
            embed.Description = $"**Результаты:** {diceEmojis[results[0]]} {diceEmojis[results[1]]}\n**Сумма:** {total}";
        }
        
        await FollowupAsync(embed: embed);
    }
    
    [SlashCommand("choice", "Случайный выбор из вариантов")]
    public async Task RandomChoice(
        [Summary("варианты", "Варианты через запятую")] string choices)
    {
        var options = choices.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        
        if (options.Count < 2)
        {
            await RespondAsync("❌ Нужно как минимум 2 варианта через запятую", ephemeral: true);
            return;
        }
        
        var choice = options[_random.Next(options.Count)];
        
        var embed = new EmbedBuilder()
            .WithTitle("🎯 Случайный выбор")
            .WithDescription($"Из **{options.Count}** вариантов я выбираю:\n\n**🎉 {choice}**")
            .AddField("Все варианты", string.Join("\n", options.Select((o, i) => $"{i + 1}. {o}")))
            .WithColor(Color.Blue)
            .WithFooter($"Запросил: {Context.User.Username}")
            .Build();
        
        await RespondAsync(embed: embed);
    }
    
    private class MemeResponse
    {
        public string title { get; set; }
        public string url { get; set; }
        public string subreddit { get; set; }
        public int ups { get; set; }
    }
    
    private class CatResponse
    {
        public string url { get; set; }
    }
}