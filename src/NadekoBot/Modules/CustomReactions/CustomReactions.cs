﻿#nullable disable
using NadekoBot.Modules.CustomReactions.Services;

namespace NadekoBot.Modules.CustomReactions;

public partial class CustomReactions : NadekoModule<CustomReactionsService>
{
    public enum All
    {
        All
    }

    private readonly IBotCredentials _creds;
    private readonly IHttpClientFactory _clientFactory;

    public CustomReactions(IBotCredentials creds, IHttpClientFactory clientFactory)
    {
        _creds = creds;
        _clientFactory = clientFactory;
    }

    private bool AdminInGuildOrOwnerInDm()
        => (ctx.Guild is null && _creds.IsOwner(ctx.User))
           || (ctx.Guild is not null && ((IGuildUser)ctx.User).GuildPermissions.Administrator);

    [Cmd]
    public async partial Task AddCustReact(string key, [Leftover] string message)
    {
        var channel = ctx.Channel as ITextChannel;
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key))
            return;

        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        var cr = await _service.AddAsync(ctx.Guild?.Id, key, message);

        await ctx.Channel.EmbedAsync(_eb.Create()
                                        .WithOkColor()
                                        .WithTitle(GetText(strs.new_cust_react))
                                        .WithDescription($"#{cr.Id}")
                                        .AddField(GetText(strs.trigger), key)
                                        .AddField(GetText(strs.response),
                                            message.Length > 1024 ? GetText(strs.redacted_too_long) : message));
    }

    [Cmd]
    public async partial Task EditCustReact(kwum id, [Leftover] string message)
    {
        var channel = ctx.Channel as ITextChannel;
        if (string.IsNullOrWhiteSpace(message) || id < 0)
            return;

        if ((channel is null && !_creds.IsOwner(ctx.User))
            || (channel is not null && !((IGuildUser)ctx.User).GuildPermissions.Administrator))
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        var cr = await _service.EditAsync(ctx.Guild?.Id, id, message);
        if (cr is not null)
            await ctx.Channel.EmbedAsync(_eb.Create()
                                            .WithOkColor()
                                            .WithTitle(GetText(strs.edited_cust_react))
                                            .WithDescription($"#{id}")
                                            .AddField(GetText(strs.trigger), cr.Trigger)
                                            .AddField(GetText(strs.response),
                                                message.Length > 1024 ? GetText(strs.redacted_too_long) : message));
        else
            await ReplyErrorLocalizedAsync(strs.edit_fail);
    }

    [Cmd]
    [Priority(1)]
    public async partial Task ListCustReact(int page = 1)
    {
        if (--page < 0 || page > 999)
            return;

        var customReactions = _service.GetCustomReactionsFor(ctx.Guild?.Id);

        if (customReactions is null || !customReactions.Any())
        {
            await ReplyErrorLocalizedAsync(strs.no_found);
            return;
        }

        await ctx.SendPaginatedConfirmAsync(page,
            curPage =>
            {
                var desc = customReactions.OrderBy(cr => cr.Trigger)
                                          .Skip(curPage * 20)
                                          .Take(20)
                                          .Select(cr => $"{(cr.ContainsAnywhere ? "🗯" : "◾")}"
                                                        + $"{(cr.DmResponse ? "✉" : "◾")}"
                                                        + $"{(cr.AutoDeleteTrigger ? "❌" : "◾")}"
                                                        + $"`{(kwum)cr.Id}` {cr.Trigger}"
                                                        + (string.IsNullOrWhiteSpace(cr.Reactions)
                                                            ? string.Empty
                                                            : " // " + string.Join(" ", cr.GetReactions())))
                                          .Join('\n');

                return _eb.Create().WithOkColor().WithTitle(GetText(strs.custom_reactions)).WithDescription(desc);
            },
            customReactions.Length,
            20);
    }

    [Cmd]
    public async partial Task ShowCustReact(kwum id)
    {
        var found = _service.GetCustomReaction(ctx.Guild?.Id, id);

        if (found is null)
        {
            await ReplyErrorLocalizedAsync(strs.no_found_id);
            return;
        }

        await ctx.Channel.EmbedAsync(_eb.Create()
                                        .WithOkColor()
                                        .WithDescription($"#{id}")
                                        .AddField(GetText(strs.trigger), found.Trigger.TrimTo(1024))
                                        .AddField(GetText(strs.response),
                                            found.Response.TrimTo(1000).Replace("](", "]\\(")));
    }

    [Cmd]
    public async partial Task DelCustReact(kwum id)
    {
        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        var cr = await _service.DeleteAsync(ctx.Guild?.Id, id);

        if (cr is not null)
            await ctx.Channel.EmbedAsync(_eb.Create()
                                            .WithOkColor()
                                            .WithTitle(GetText(strs.deleted))
                                            .WithDescription($"#{id}")
                                            .AddField(GetText(strs.trigger), cr.Trigger.TrimTo(1024))
                                            .AddField(GetText(strs.response), cr.Response.TrimTo(1024)));
        else
            await ReplyErrorLocalizedAsync(strs.no_found_id);
    }

    [Cmd]
    public async partial Task CrReact(kwum id, params string[] emojiStrs)
    {
        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        var cr = _service.GetCustomReaction(ctx.Guild?.Id, id);
        if (cr is null)
        {
            await ReplyErrorLocalizedAsync(strs.no_found);
            return;
        }

        if (emojiStrs.Length == 0)
        {
            await _service.ResetCrReactions(ctx.Guild?.Id, id);
            await ReplyConfirmLocalizedAsync(strs.crr_reset(Format.Bold(id.ToString())));
            return;
        }

        var succ = new List<string>();
        foreach (var emojiStr in emojiStrs)
        {
            var emote = emojiStr.ToIEmote();

            // i should try adding these emojis right away to the message, to make sure the bot can react with these emojis. If it fails, skip that emoji
            try
            {
                await ctx.Message.AddReactionAsync(emote);
                await Task.Delay(100);
                succ.Add(emojiStr);

                if (succ.Count >= 3)
                    break;
            }
            catch { }
        }

        if (succ.Count == 0)
        {
            await ReplyErrorLocalizedAsync(strs.invalid_emojis);
            return;
        }

        await _service.SetCrReactions(ctx.Guild?.Id, id, succ);


        await ReplyConfirmLocalizedAsync(strs.crr_set(Format.Bold(id.ToString()),
            string.Join(", ", succ.Select(x => x.ToString()))));
    }

    [Cmd]
    public partial Task CrCa(kwum id)
        => InternalCrEdit(id, CustomReactionsService.CrField.ContainsAnywhere);

    [Cmd]
    public partial Task CrDm(kwum id)
        => InternalCrEdit(id, CustomReactionsService.CrField.DmResponse);

    [Cmd]
    public partial Task CrAd(kwum id)
        => InternalCrEdit(id, CustomReactionsService.CrField.AutoDelete);

    [Cmd]
    public partial Task CrAt(kwum id)
        => InternalCrEdit(id, CustomReactionsService.CrField.AllowTarget);

    [Cmd]
    [OwnerOnly]
    public async partial Task CrsReload()
    {
        await _service.TriggerReloadCustomReactions();

        await ctx.OkAsync();
    }

    private async Task InternalCrEdit(kwum id, CustomReactionsService.CrField option)
    {
        var cr = _service.GetCustomReaction(ctx.Guild?.Id, id);
        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        var (success, newVal) = await _service.ToggleCrOptionAsync(id, option);
        if (!success)
        {
            await ReplyErrorLocalizedAsync(strs.no_found_id);
            return;
        }

        if (newVal)
            await ReplyConfirmLocalizedAsync(strs.option_enabled(Format.Code(option.ToString()),
                Format.Code(id.ToString())));
        else
            await ReplyConfirmLocalizedAsync(strs.option_disabled(Format.Code(option.ToString()),
                Format.Code(id.ToString())));
    }

    [Cmd]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPerm.Administrator)]
    public async partial Task CrClear()
    {
        if (await PromptUserConfirmAsync(_eb.Create()
                                            .WithTitle("Custom reaction clear")
                                            .WithDescription("This will delete all custom reactions on this server.")))
        {
            var count = _service.DeleteAllCustomReactions(ctx.Guild.Id);
            await ReplyConfirmLocalizedAsync(strs.cleared(count));
        }
    }

    [Cmd]
    public async partial Task CrsExport()
    {
        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        _ = ctx.Channel.TriggerTypingAsync();

        var serialized = _service.ExportCrs(ctx.Guild?.Id);
        await using var stream = await serialized.ToStream();
        await ctx.Channel.SendFileAsync(stream, "crs-export.yml");
    }

    [Cmd]
#if GLOBAL_NADEKO
        [OwnerOnly]
#endif
    public async partial Task CrsImport([Leftover] string input = null)
    {
        if (!AdminInGuildOrOwnerInDm())
        {
            await ReplyErrorLocalizedAsync(strs.insuff_perms);
            return;
        }

        input = input?.Trim();

        _ = ctx.Channel.TriggerTypingAsync();

        if (input is null)
        {
            var attachment = ctx.Message.Attachments.FirstOrDefault();
            if (attachment is null)
            {
                await ReplyErrorLocalizedAsync(strs.expr_import_no_input);
                return;
            }

            using var client = _clientFactory.CreateClient();
            input = await client.GetStringAsync(attachment.Url);

            if (string.IsNullOrWhiteSpace(input))
            {
                await ReplyErrorLocalizedAsync(strs.expr_import_no_input);
                return;
            }
        }

        var succ = await _service.ImportCrsAsync(ctx.Guild?.Id, input);
        if (!succ)
        {
            await ReplyErrorLocalizedAsync(strs.expr_import_invalid_data);
            return;
        }

        await ctx.OkAsync();
    }
}