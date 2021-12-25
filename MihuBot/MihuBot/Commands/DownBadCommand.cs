﻿using MihuBot.DownBadProviders;

namespace MihuBot.Commands
{
    public sealed class DownBadCommand : CommandBase
    {
        public override string Command => "downbad";

        private readonly DiscordSocketClient _discord;
        private readonly IDownBadProvider[] _providers;
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, (ulong Channel, List<string> Sources)>> _registrations = new("DownBadRegistrations.json");

        public DownBadCommand(DiscordSocketClient discord, IEnumerable<IDownBadProvider> downBadProviders)
        {
            _discord = discord;
            _providers = downBadProviders.ToArray();
        }

        public override async Task InitAsync()
        {
            var registrations = await _registrations.QueryAsync(i => i.ToDictionary(i => i.Key, i => i.Value));

            _discord.GuildMembersDownloaded += async guild =>
            {
                List<string> sources = null;
                lock (registrations)
                {
                    if (registrations.TryGetValue(guild.Id, out var registration))
                    {
                        registrations.Remove(guild.Id);
                        sources = registration.Sources;
                    }
                }

                if (sources is not null)
                {
                    await RegisterAllAsync(guild, sources);
                }
            };

            List<(SocketGuild Guild, List<string> Sources)> guildSubscriptions = new();

            lock (registrations)
            {
                foreach (var registration in registrations)
                {
                    if (_discord.GetGuild(registration.Key) is SocketGuild guild)
                    {
                        if (registration.Value.Sources is List<string> sources)
                        {
                            guildSubscriptions.Add((guild, sources));
                        }
                    }
                }

                foreach (var (guild, _) in guildSubscriptions)
                {
                    registrations.Remove(guild.Id);
                }
            }

            foreach (var (guild, sources) in guildSubscriptions)
            {
                await RegisterAllAsync(guild, sources);
            }

            async Task RegisterAllAsync(SocketGuild guild, List<string> sources)
            {
                foreach (string source in sources)
                {
                    var url = new Uri(source, UriKind.Absolute);
                    var provider = _providers.FirstOrDefault(p => p.CanMatch(url));
                    if (provider is not null)
                    {
                        await TryRegisterAsync(guild, provider, url);
                    }
                }
            }
        }

        public async override Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync(Command))
            {
                return;
            }

            ulong channelId = 0;
            var registrations = await _registrations.EnterAsync();
            try
            {
                if (registrations.TryGetValue(ctx.Guild.Id, out var registration))
                {
                    channelId = registration.Channel;
                }
                registration.Sources ??= new List<string>();
                registrations[ctx.Guild.Id] = registration;
                int sourceCount = registration.Sources.Count;

                if (ctx.Arguments.Length > 0 &&
                    ctx.Arguments[0].Equals("register", StringComparison.OrdinalIgnoreCase))
                {
                    var channel = ctx.Message.MentionedChannels.OfType<SocketTextChannel>().FirstOrDefault();
                    if (channel is null)
                    {
                        if (ctx.Arguments.Length == 1 ||
                            !ulong.TryParse(ctx.Arguments[1], out channelId) ||
                            (channel = ctx.Discord.GetTextChannel(channelId)) is null)
                        {
                            await ctx.ReplyAsync("Which channel do you want to register to?");
                            return;
                        }
                    }

                    registrations[ctx.Guild.Id] = (channel.Id, registration.Sources);
                    return;
                }

                if (ctx.Arguments.Length == 0 || channelId == 0)
                {
                    if (channelId == 0 || ctx.Discord.GetTextChannel(channelId) is not SocketTextChannel channel)
                    {
                        await ctx.ReplyAsync("Not registered to a channel yet. Use `!downbad register channel`");
                    }
                    else
                    {
                        await ctx.ReplyAsync($"Registered to {channel.Mention}, watching {sourceCount} sources");
                    }
                    return;
                }

                if (Uri.TryCreate(ctx.Arguments[0], UriKind.Absolute, out Uri url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
                {
                    foreach (IDownBadProvider provider in _providers)
                    {
                        if (provider.CanMatch(url))
                        {
                            if (registration.Sources.Any(s => s.Equals(url.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                            {
                                await ctx.ReplyAsync($"Already watching <{url.AbsoluteUri}>");
                                return;
                            }

                            string error = await TryRegisterAsync(ctx.Guild, provider, url);
                            if (error is not null)
                            {
                                await ctx.ReplyAsync(error);
                            }
                            else
                            {
                                registration.Sources.Add(url.AbsoluteUri);
                                await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                            }
                            return;
                        }
                    }
                }
            }
            finally
            {
                _registrations.Exit();
            }

            await ctx.ReplyAsync("Can't provide such down-badness :(");
        }

        private async Task<string> TryRegisterAsync(SocketGuild guild, IDownBadProvider provider, Uri url)
        {
            return await provider.TryWatchAsync(url, async () =>
            {
                ulong channelId = await _registrations.QueryAsync(i => i.TryGetValue(guild.Id, out var registration) ? registration.Channel : 0);
                if (_discord.GetTextChannel(channelId) is SocketTextChannel channel)
                {
                    return channel;
                }
                return null;
            });
        }
    }
}