﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;

namespace ForumCrawler
{
    [Group("warn")]
    [Alias("warning", "warns", "warnings")]
    public class WarningCommands : InteractiveBase<SocketCommandContext>
    {
        private static EmbedBuilder AddWarningsToEmbed(EmbedBuilder embed, WarningState state)
        {
            if (state.Warnings > 0)
            {
                embed.AddField($"Warnings ({state.Warnings})", string.Concat(Enumerable.Repeat("⚠️ ", state.Warnings)),
                    true);
                embed.WithColor(Color.Orange);
            }

            if (state.Strikes > 0)
            {
                embed.AddField($"Strikes ({state.Strikes})", string.Concat(Enumerable.Repeat("❌ ", state.Strikes)),
                    true);
                embed.WithColor(Color.Red);
            }

            if (state.Warnings == 0 && state.Strikes == 0)
            {
                embed.AddField("Warnings (0)", "No warnings, well done!");
                embed.WithColor(Color.Green);
            }
            else if (state.MutedUntil.HasValue)
            {
                embed.AddField("Strike mute penalty expires in",
                    (state.MutedUntil.Value - DateTimeOffset.UtcNow).ToHumanReadableString());
            }
            else if (state.LastTick > DateTimeOffset.UtcNow)
            {
                embed.AddField("Probation ends in", (state.LastTick - DateTimeOffset.UtcNow).ToHumanReadableString());
            }
            else
            {
                var next = state.Warnings == 0 ? "strike" : "warning";
                var duration = state.Warnings == 0
                    ? TimeSpan.FromDays(WarningState.WarningDecayLogicV3.StrikeExpiryDays)
                    : TimeSpan.FromDays(WarningState.WarningDecayLogicV3.GetWarningExpiryDays(state));
                embed.AddField($"Next {next} expires in",
                    (duration - (DateTimeOffset.UtcNow - state.LastTick)).ToHumanReadableString());
            }

            return embed;
        }

        private static EmbedBuilder GetWarningEmbed(IUser user, Warning warning)
        {
            return new EmbedBuilder()
                .WithAuthor(user)
                .WithColor(Color.Gold)
                .AddField("User", MentionUtils.MentionUser(warning.UserId), true)
                .AddField("Moderator", MentionUtils.MentionUser(warning.IssuerId), true)
                .AddField("Channel", MentionUtils.MentionChannel(warning.ChannelId), true)
                .AddField("Reason", warning.Reason)
                .WithFooter("ID: " + warning.Id)
                .WithCurrentTimestamp();
        }

        private EmbedBuilder GetWarningIssuedEmbed(IUser user, Warning warning, WarningState state)
        {
            return (warning.Amount == 0
                    ? GetWarningEmbed(user, warning)
                    : AddWarningsToEmbed(GetWarningEmbed(user, warning), state))
                .WithTitle($"**{warning.Type}**");
        }

        private async Task EditWarningInternalAsync(IUser user, long id, string reason, bool force)
        {
            await Database.UNSAFE_WithWarningsAsync(user.Id, async history =>
            {
                var warning = Array.Find(history, w => w.Id == id);
                if (warning == null)
                {
                    throw new Exception("Invalid warning ID.");
                }

                if (warning.UserId != user.Id) // this check exists to catch typos in warning IDs
                {
                    throw new Exception("Warning ID does not match with user!");
                }

                if (warning.IssuerId != Context.User.Id && !force)
                {
                    throw new Exception("Warning can only be edited by its issuer, " +
                                        MentionUtils.MentionUser(warning.IssuerId) + ".");
                }

                var oldReason = warning.Reason;
                warning.Reason = reason ?? warning.Reason;

                var tempEmbed = GetWarningEmbed(user, warning)
                    .WithTitle("**A warning is being edited...**")
                    .WithColor(Color.Blue)
                    .Build();

                var oldSeverity = warning.Type;
                var state = await ApplyWarningWithSeverity(user, history, warning, tempEmbed);
                if (warning.Type != oldSeverity || warning.Reason != oldReason)
                {
                    var embed = GetWarningIssuedEmbed(user, warning, state);
                    if (warning.Type != oldSeverity)
                    {
                        if (warning.Amount == 0)
                        {
                            AddWarningsToEmbed(embed, state);
                        }

                        embed.Title += $" (Changed from {oldSeverity})";
                    }

                    if (warning.Reason != oldReason)
                    {
                        embed.Title += " (Reason updated)";
                        embed.Fields.Insert(3, new EmbedFieldBuilder().WithName("Old Reason").WithValue(oldReason));
                    }

                    await AnnounceWarningEverywhereAsync(user, embed.Build());
                }
            });
        }

        private async Task RemoveWarningInternalAsync(IUser user, long id, string reason, bool force)
        {
            var warning = await Database.UNSAFE_GetWarningAsync(id);
            if (warning == null)
            {
                throw new Exception("Invalid warning ID.");
            }

            if (warning.UserId != user.Id) // this check exists to catch typos in warning IDs
            {
                throw new Exception("Warning ID does not match with user!");
            }

            if (warning.IssuerId != Context.User.Id && !force)
            {
                throw new Exception("Warning can only be deleted by its issuer, " +
                                    MentionUtils.MentionUser(warning.IssuerId) + ".");
            }

            await Database.UNSAFE_RemoveWarningAsync(id, Context.Message, reason);

            var state = WarningState.FromDatabase(await Database.UNSAFE_GetWarningsAsync(user.Id));
            var embed = AddWarningsToEmbed(GetWarningEmbed(user, warning), state)
                .WithDescription("**A warning was deleted.**")
                .Build();

            await AnnounceWarningEverywhereAsync(user, embed);
        }

        private string GetUnmentionedUser(ulong userId)
        {
            var user = Context.Client.GetGuild(DiscordSettings.GuildId).GetUser(userId);

            if (user == null)
            {
                return "Deleted User";
			}

            return user.Username.DiscordEscape() + "#" + user.Discriminator;
        }

        private async Task ListWarningsAsync(string message, ICollection<Warning> warnings, IMessageChannel target)
        {
            var warningEmbeds = new List<EmbedBuilder> {new EmbedBuilder()};
            foreach (var w in warnings)
            {
                var warningStr =
                    $"**{(w.Amount == 0 ? "Initial Warning" : w.Amount == 1 ? "Warning" : "Strike")} {w.Id} " +
                    $"({w.IssueDate:MM/dd/yyyy}{(w.RemoveDate.HasValue ? $", removed on {w.RemoveDate.Value:MM/dd/yyyy}" : "")})**\n" +
                    $"{w.Reason}\n" +
                    (w.RemoveReason == null ? "" : $"Remove Reason: {w.RemoveReason}\n") +
                    $"[{GetUnmentionedUser(w.IssuerId)}](https://discordapp.com/channels/{DiscordSettings.GuildId}/{w.ChannelId}/{w.MessageId}) " +
                    $"in {MentionUtils.MentionChannel(w.ChannelId)} " +
                    (w.RemoveChannelId.HasValue && w.RemoveMessageId.HasValue && w.RemoverId.HasValue
                        ? $" | Removed by [{GetUnmentionedUser(w.RemoverId.Value)}](https://discordapp.com/channels/{DiscordSettings.GuildId}/{w.RemoveChannelId.Value}/{w.RemoveMessageId.Value}) in {MentionUtils.MentionChannel(w.ChannelId)}\n\n"
                        : "\n\n");

                if (warningEmbeds.Last().Description?.Length + warningStr.Length > 2048)
                {
                    warningEmbeds.Add(new EmbedBuilder());
                }

                warningEmbeds.Last().Description += warningStr;
            }

            await target.SendMessageAsync(message, embed: warnings.Count > 0 ? warningEmbeds[0].Build() : null);

            foreach (var embedBuilder in warningEmbeds.Skip(1))
            {
                await target.SendMessageAsync(embed: embedBuilder.Build());
            }
        }

        private Criteria<SocketMessage> GetCriteria(IMessageChannel channel)
        {
            var criteria = new Criteria<SocketMessage>();
            criteria.AddCriterion(new EnsureFromChannelCriterion(channel));
            criteria.AddCriterion(new EnsureFromUserCriterion(Context.User));
            return criteria;
        }

        [Command("list")]
        [RequireChannel(DiscordSettings.BotCommandsChannel)]
        [Priority(1)]
        public async Task ListWarns(IUser user = null)
        {
            if (user == null)
            {
                user = Context.User;
            }

            var warnings = await Database.UNSAFE_GetWarningsAsync(user.Id);
            await ListWarningsAsync($"**{GetUnmentionedUser(user.Id)}** has {warnings.Length} warnings.", warnings,
                Context.Channel);
        }

        [Command("status")]
        [Alias("stats")]
        [RequireChannel(DiscordSettings.BotCommandsChannel)]
        [Priority(1)]
        public async Task WarnStats(IUser user = null)
        {
            if (user == null)
            {
                user = Context.User;
            }

            var state = WarningState.FromDatabase(await Database.UNSAFE_GetWarningsAsync(user.Id));
            var embed = AddWarningsToEmbed(new EmbedBuilder().WithAuthor(user), state).Build();
            await ReplyAsync(embed: embed);
        }

        [Command("force remove")]
        [RequireRole(DiscordSettings.DiscordServerOwner)]
        [Priority(1)]
        public async Task ForceRemoveWarn(IUser user, long id, [Remainder] string reason)
        {
            await RemoveWarningInternalAsync(user, id, reason, true);
        }

        [Command("remove")]
        [RequireRole(DiscordSettings.DiscordStaff)]
        [Priority(1)]
        public async Task RemoveWarn(IUser user, long id, [Remainder] string reason)
        {
            await RemoveWarningInternalAsync(user, id, reason, false);
        }

        [Command("force edit")]
        [RequireRole(DiscordSettings.DiscordStaff)]
        [Priority(1)]
        public async Task ForceEditWarn(IUser user, long id, [Remainder] string reason = null)
        {
            await EditWarningInternalAsync(user, id, reason, true);
        }

        [Command("edit")]
        [RequireRole(DiscordSettings.DiscordStaff)]
        [Priority(1)]
        public async Task EditWarn(IUser user, long id, [Remainder] string reason = null)
        {
            await EditWarningInternalAsync(user, id, reason, false);
        }

        [Command]
        [RequireRole(DiscordSettings.DiscordStaff)]
        [Priority(0)]
        public async Task Warn(IGuildUser user, [Remainder] string reason)
        {
            if (user.IsStaff())
            {
                throw new Exception("User is staff.");
            }

            var history = await Database.UNSAFE_GetWarningsAsync(user.Id);

            var warning = new Warning
            {
                UserId = user.Id,
                ChannelId = Context.Channel.Id,
                MessageId = Context.Message.Id,
                IssuerId = Context.User.Id,
                IssueDate = DateTime.UtcNow,
                Reason = reason
            };

            var tempEmbed = GetWarningEmbed(user, warning)
                .WithTitle("**A warning is being issued...**")
                .WithColor(Color.Blue)
                .WithFooter("")
                .Build();

            var msg = await ReplyAsync(embed: tempEmbed);
            try
            {
                var state = await ApplyWarningWithSeverity(user, history, warning, tempEmbed);
                await Database.UNSAFE_AddWarningAsync(warning);

                var embed = GetWarningIssuedEmbed(user, warning, state);
                await AnnounceWarningEverywhereAsync(user, embed.Build());
            }
            catch (Exception ex)
            {
                await Context.User.SendMessageAsync(ex.Message);
            }
            finally
            {
                await msg.DeleteAsync();
            }
        }

        private async Task AnnounceWarningEverywhereAsync(IUser user, Embed embed)
        {
            await ReplyAsync(embed: embed);
            await Context.User.SendMessageAsync(embed: embed);
            await user.SendMessageAsync(embed: embed);
            await Context.Client
                .GetGuild(DiscordSettings.GuildId)
                .GetTextChannel(DiscordSettings.LogsChannel)
                .SendMessageAsync(embed: embed);
        }

        private async Task<WarningState> ApplyWarningWithSeverity(IUser user, Warning[] history, Warning warning,
            Embed tempEmbed)
        {
            var state = WarningState.FromDatabase(history);

            warning.Amount = await RequestSeverity(user, history,
                AddWarningsToEmbed(tempEmbed.ToEmbedBuilder(), state).Build());
            if (history.Contains(warning))
            {
                state = WarningState.FromDatabase(history);
            }
            else
            {
                state.Add(warning.Amount, warning.IssueDate);
            }

            if (state.MutedUntil.HasValue)
            {
                await MuteWatcher.MuteUser(
                    new Mute
                    {
                        UserId = user.Id,
                        IssuerId = Context.Client.GetGuild(DiscordSettings.GuildId).CurrentUser.Id,
                        IssueDate = DateTime.UtcNow,
                        ExpiryDate = state.MutedUntil.Value.UtcDateTime
                    }, "You got a strike!", true, true);
            }
            else
            {
                await MuteWatcher.UnmuteUser(user.Id, Context.Client.GetGuild(DiscordSettings.GuildId).CurrentUser.Id);
            }

            return state;
        }

        private async Task<int> RequestSeverity(IUser user, Warning[] history, Embed embed)
        {
            var initialWarnings = history.Where(w => w.Amount == 0 && !w.RemoveDate.HasValue).ToList();
            var dmChannel = await Context.User.GetOrCreateDMChannelAsync();
            await ListWarningsAsync($"{GetUnmentionedUser(user.Id)} has {initialWarnings.Count} initial warnings.",
                initialWarnings, dmChannel);
            await dmChannel.SendMessageAsync(embed: embed);
            await dmChannel.SendMessageAsync(
                "Select warning severity:\n" +
                "```swift\n" +
                "0 - Initial Warning (first violation of a specific rule)\n" +
                "1 - Warning\n" +
                "3 - Strike\n" +
                "```");
            var msg = await NextMessageAsync(GetCriteria(dmChannel), TimeSpan.FromMinutes(5));
            if (msg == null)
            {
                throw new Exception("Timeout, operation cancelled.");
            }

            if (!int.TryParse(msg.Content, out var amount))
            {
                throw new Exception("Invalid input, operation cancelled.");
            }

            if (amount != 0 && amount != 1 && amount != 3)
            {
                throw new Exception("Invalid input, operation cancelled.");
            }

            return amount;
        }
    }
}