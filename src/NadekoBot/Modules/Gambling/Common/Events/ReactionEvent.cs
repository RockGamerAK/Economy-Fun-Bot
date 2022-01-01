﻿#nullable disable
using NadekoBot.Services.Database.Models;

namespace NadekoBot.Modules.Gambling.Common.Events;

public class ReactionEvent : ICurrencyEvent
{
    public event Func<ulong, Task> OnEnded;
    private long PotSize { get; set; }
    public bool Stopped { get; private set; }
    public bool PotEmptied { get; private set; }
    private readonly DiscordSocketClient _client;
    private readonly IGuild _guild;
    private IUserMessage _msg;
    private IEmote _emote;
    private readonly ICurrencyService _cs;
    private readonly long _amount;

    private readonly Func<CurrencyEvent.Type, EventOptions, long, IEmbedBuilder> _embedFunc;
    private readonly bool _isPotLimited;
    private readonly ITextChannel _channel;
    private readonly ConcurrentHashSet<ulong> _awardedUsers = new();
    private readonly ConcurrentQueue<ulong> _toAward = new();
    private readonly Timer _t;
    private readonly Timer _timeout;
    private readonly bool _noRecentlyJoinedServer;
    private readonly EventOptions _opts;
    private readonly GamblingConfig _config;

    private readonly object stopLock = new();

    private readonly object potLock = new();

    public ReactionEvent(
        DiscordSocketClient client,
        ICurrencyService cs,
        SocketGuild g,
        ITextChannel ch,
        EventOptions opt,
        GamblingConfig config,
        Func<CurrencyEvent.Type, EventOptions, long, IEmbedBuilder> embedFunc)
    {
        _client = client;
        _guild = g;
        _cs = cs;
        _amount = opt.Amount;
        PotSize = opt.PotSize;
        _embedFunc = embedFunc;
        _isPotLimited = PotSize > 0;
        _channel = ch;
        _noRecentlyJoinedServer = false;
        _opts = opt;
        _config = config;

        _t = new(OnTimerTick, null, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(2));
        if (_opts.Hours > 0)
            _timeout = new(EventTimeout, null, TimeSpan.FromHours(_opts.Hours), Timeout.InfiniteTimeSpan);
    }

    private void EventTimeout(object state)
    {
        var _ = StopEvent();
    }

    private async void OnTimerTick(object state)
    {
        var potEmpty = PotEmptied;
        var toAward = new List<ulong>();
        while (_toAward.TryDequeue(out var x)) toAward.Add(x);

        if (!toAward.Any())
            return;

        try
        {
            await _cs.AddBulkAsync(toAward, toAward.Select(_ => "Reaction Event"), toAward.Select(_ => _amount), true);

            if (_isPotLimited)
                await _msg.ModifyAsync(m =>
                    {
                        m.Embed = GetEmbed(PotSize).Build();
                    },
                    new() { RetryMode = RetryMode.AlwaysRetry });

            Log.Information("Awarded {0} users {1} currency.{2}",
                toAward.Count,
                _amount,
                _isPotLimited ? $" {PotSize} left." : "");

            if (potEmpty)
            {
                var _ = StopEvent();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error adding bulk currency to users");
        }
    }

    public async Task StartEvent()
    {
        if (Emote.TryParse(_config.Currency.Sign, out var emote))
            _emote = emote;
        else
            _emote = new Emoji(_config.Currency.Sign);
        _msg = await _channel.EmbedAsync(GetEmbed(_opts.PotSize));
        await _msg.AddReactionAsync(_emote);
        _client.MessageDeleted += OnMessageDeleted;
        _client.ReactionAdded += HandleReaction;
        _t.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private IEmbedBuilder GetEmbed(long pot)
        => _embedFunc(CurrencyEvent.Type.Reaction, _opts, pot);

    private async Task OnMessageDeleted(Cacheable<IMessage, ulong> msg, Cacheable<IMessageChannel, ulong> cacheable)
    {
        if (msg.Id == _msg.Id) await StopEvent();
    }

    public async Task StopEvent()
    {
        await Task.Yield();
        lock (stopLock)
        {
            if (Stopped)
                return;
            Stopped = true;
            _client.MessageDeleted -= OnMessageDeleted;
            _client.ReactionAdded -= HandleReaction;
            _t.Change(Timeout.Infinite, Timeout.Infinite);
            _timeout?.Change(Timeout.Infinite, Timeout.Infinite);
            try
            {
                var _ = _msg.DeleteAsync();
            }
            catch { }

            var os = OnEnded(_guild.Id);
        }
    }

    private Task HandleReaction(
        Cacheable<IUserMessage, ulong> msg,
        Cacheable<IMessageChannel, ulong> cacheable,
        SocketReaction r)
    {
        var _ = Task.Run(() =>
        {
            if (_emote.Name != r.Emote.Name)
                return;
            if ((r.User.IsSpecified
                    ? r.User.Value
                    : null) is not IGuildUser gu // no unknown users, as they could be bots, or alts
                || msg.Id != _msg.Id // same message
                || gu.IsBot // no bots
                || (DateTime.UtcNow - gu.CreatedAt).TotalDays <= 5 // no recently created accounts
                || (_noRecentlyJoinedServer
                    && // if specified, no users who joined the server in the last 24h
                    (gu.JoinedAt is null
                     || (DateTime.UtcNow - gu.JoinedAt.Value).TotalDays
                     < 1))) // and no users for who we don't know when they joined
                return;
            // there has to be money left in the pot
            // and the user wasn't rewarded
            if (_awardedUsers.Add(r.UserId) && TryTakeFromPot())
            {
                _toAward.Enqueue(r.UserId);
                if (_isPotLimited && PotSize < _amount)
                    PotEmptied = true;
            }
        });
        return Task.CompletedTask;
    }

    private bool TryTakeFromPot()
    {
        if (_isPotLimited)
            lock (potLock)
            {
                if (PotSize < _amount)
                    return false;

                PotSize -= _amount;
                return true;
            }

        return true;
    }
}