﻿using Discord;
using Discord.Commands;
using NadekoBot.Common.Attributes;
using NadekoBot.Common;
using NadekoBot.Extensions;
using NadekoBot.Modules.Utility.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Utility
{
    public partial class Utility
    {
        [Group]
        public class UnitConverterCommands : NadekoSubmodule<ConverterService>
        {
            [NadekoCommand, Aliases]
            public async Task ConvertList()
            {
                var units = _service.Units;

                var embed = _eb.Create()
                    .WithTitle(GetText(strs.convertlist))
                    .WithOkColor();


                foreach (var g in units.GroupBy(x => x.UnitType))
                {
                    embed.AddField(g.Key.ToTitleCase(),
                        String.Join(", ", g.Select(x => x.Triggers.FirstOrDefault()).OrderBy(x => x)));
                }
                
                await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            }

            [NadekoCommand, Aliases]
            [Priority(0)]
            public async Task Convert(string origin, string target, decimal value)
            {
                var originUnit = _service.Units.FirstOrDefault(x => x.Triggers.Select(y => y.ToUpperInvariant()).Contains(origin.ToUpperInvariant()));
                var targetUnit = _service.Units.FirstOrDefault(x => x.Triggers.Select(y => y.ToUpperInvariant()).Contains(target.ToUpperInvariant()));
                if (originUnit is null || targetUnit is null)
                {
                    await ReplyErrorLocalizedAsync("convert_not_found", Format.Bold(origin), Format.Bold(target)).ConfigureAwait(false);
                    return;
                }
                if (originUnit.UnitType != targetUnit.UnitType)
                {
                    await ReplyErrorLocalizedAsync("convert_type_error", Format.Bold(originUnit.Triggers.First()), Format.Bold(targetUnit.Triggers.First())).ConfigureAwait(false);
                    return;
                }
                decimal res;
                if (originUnit.Triggers == targetUnit.Triggers) res = value;
                else if (originUnit.UnitType == "temperature")
                {
                    //don't really care too much about efficiency, so just convert to Kelvin, then to target
                    switch (originUnit.Triggers.First().ToUpperInvariant())
                    {
                        case "C":
                            res = value + 273.15m; //celcius!
                            break;
                        case "F":
                            res = (value + 459.67m) * (5m / 9m);
                            break;
                        default:
                            res = value;
                            break;
                    }
                    //from Kelvin to target
                    switch (targetUnit.Triggers.First().ToUpperInvariant())
                    {
                        case "C":
                            res = res - 273.15m; //celcius!
                            break;
                        case "F":
                            res = res * (9m / 5m) - 459.67m;
                            break;
                    }
                }
                else
                {
                    if (originUnit.UnitType == "currency")
                    {
                        res = (value * targetUnit.Modifier) / originUnit.Modifier;
                    }
                    else
                        res = (value * originUnit.Modifier) / targetUnit.Modifier;
                }
                res = Math.Round(res, 4);

                await SendConfirmAsync(GetText("convert", value, originUnit.Triggers.Last(), res, targetUnit.Triggers.Last())).ConfigureAwait(false);
            }
        }
    }
}