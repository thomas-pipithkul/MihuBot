﻿using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MihuBot.Commands;
using MihuBot.Helpers;
using SharpCollections.Generic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public class MihuBotService : IHostedService
    {
        private readonly HttpClient _http;
        private readonly DiscordSocketClient _discord;

        private readonly CompactPrefixTree<CommandBase> _commands = new CompactPrefixTree<CommandBase>(ignoreCase: true);
        private readonly List<INonCommandHandler> _nonCommandHandlers = new List<INonCommandHandler>();

        public MihuBotService(IServiceProvider services, HttpClient httpClient, DiscordSocketClient discord)
        {
            _http = httpClient;
            _discord = discord;

            foreach (var type in Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract))
            {
                if (typeof(CommandBase).IsAssignableFrom(type))
                {
                    var instance = ActivatorUtilities.CreateInstance(services, type) as CommandBase;
                    _commands.Add(instance.Command, instance);
                    foreach (string alias in instance.Aliases)
                    {
                        _commands.Add(alias, instance);
                    }
                    _nonCommandHandlers.Add(instance);
                }
                else if (typeof(NonCommandHandler).IsAssignableFrom(type))
                {
                    var instance = ActivatorUtilities.CreateInstance(services, type) as NonCommandHandler;
                    _nonCommandHandlers.Add(instance);
                }
            }
        }

        private async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (!(reaction.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                if (reaction.Emote.Name.Equals("yesw", StringComparison.OrdinalIgnoreCase))
                {
                    if (reaction.User.IsSpecified && reaction.User.Value.Id == KnownUsers.MihuBot)
                        return;

                    var userMessage = reaction.Message;

                    if (userMessage.IsSpecified)
                    {
                        await userMessage.Value.AddReactionAsync(Emotes.YesW);
                    }
                }
                else if (reaction.Emote is Emote reactionEmote)
                {
                    if (reactionEmote.Id == 685587814330794040ul) // James emote
                    {
                        var userMessage = reaction.Message;

                        if (userMessage.IsSpecified)
                        {
                            foreach (var emote in Emotes.JamesEmotes)
                            {
                                await userMessage.Value.AddReactionAsync(emote);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task Client_MessageReceived(SocketMessage message)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                return;

            if (message.Author.Id == KnownUsers.Miha && message.Attachments.Any())
            {
                Attachment mcFunction = message.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase));

                if (mcFunction != null)
                {
                    string functionsFile = await _http.GetStringAsync(mcFunction.Url);
                    string[] functions = functionsFile
                        .Replace('\r', '\n')
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(f => f.Trim().Length > 0)
                        .Select(f => "execute positioned as MihuBot run " + f)
                        .ToArray();

                    await message.ReplyAsync($"Running {functions.Length} commands");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            StringBuilder sb = new StringBuilder();

                            await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback false");

                            for (int i = 0; i < functions.Length; i += 100)
                            {
                                Task<string>[] tasks = functions
                                    .AsMemory(i, Math.Min(100, functions.Length - i))
                                    .ToArray()
                                    .Select(f => McCommand.RunMinecraftCommandAsync(f))
                                    .ToArray();

                                await Task.WhenAll(tasks);

                                foreach (var task in tasks)
                                    sb.AppendLine(task.Result);
                            }

                            await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback true");

                            var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                            await message.Channel.SendFileAsync(ms, "responses.txt");
                        }
                        catch { }
                    });
                }
            }

            if (message.Author.Id == KnownUsers.MihuBot)
                return;

            if (guildChannel.Guild.Id == Guilds.DDs)
            {
                if (guildChannel.Id == Channels.DDsGeneral)
                    return; // Ignore
            }

            await HandleMessageAsync(message);
        }

        private async Task HandleMessageAsync(SocketMessage message)
        {
            if (message.Content.Contains('\0'))
                throw new InvalidOperationException("Null in text");

            var content = message.Content.TrimStart();

            if (content.StartsWith('!') || content.StartsWith('/') || content.StartsWith('-'))
            {
                int spaceIndex = content.AsSpan().IndexOfAny(' ', '\r', '\n');

                if (_commands.TryMatchExact(spaceIndex == -1 ? content.AsSpan(1) : content.AsSpan(1, spaceIndex - 1), out var match))
                {
                    var command = match.Value;
                    var context = new CommandContext(_discord, message);

                    if (command.TryEnter(context, out TimeSpan cooldown, out bool shouldWarn))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await command.ExecuteAsync(context);
                            }
                            catch (Exception ex)
                            {
                                await context.DebugAsync(ex.ToString());
                            }
                        });
                    }
                    else if (shouldWarn)
                    {
                        await context.WarnCooldownAsync(cooldown);
                    }
                }
            }
            else
            {
                var messageContext = new MessageContext(_discord, message);

                foreach (var handler in _nonCommandHandlers)
                {
                    try
                    {
                        Task task = handler.HandleAsync(messageContext);

                        if (task.IsCompleted)
                        {
                            if (!task.IsCompletedSuccessfully)
                                await task;
                        }
                        else
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await task;
                                }
                                catch (Exception ex)
                                {
                                    await messageContext.DebugAsync(ex.ToString());
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _ = Task.Run(async () => await messageContext.DebugAsync(ex.ToString()));
                    }
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var handler in _nonCommandHandlers)
            {
                if (handler is CommandBase commandBase)
                {
                    await commandBase.InitAsync();
                }
                else if (handler is NonCommandHandler nonCommand)
                {
                    await nonCommand.InitAsync();
                }
                else throw new InvalidOperationException(handler.GetType().FullName);
            }

            TaskCompletionSource<object> onConnectedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _discord.Connected += () => { onConnectedTcs.TrySetResult(null); return Task.CompletedTask; };

            TaskCompletionSource<object> onReadyTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _discord.Ready += () => { onReadyTcs.TrySetResult(null); return Task.CompletedTask; };

            _discord.MessageReceived += async message =>
            {
                try
                {
                    await Client_MessageReceived(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            };

            _discord.ReactionAdded += Client_ReactionAdded;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await _discord.LoginAsync(TokenType.Bot, Secrets.Discord.AuthToken);
                await _discord.StartAsync();

                await onConnectedTcs.Task;
                await onReadyTcs.Task;

                await Logger.Instance.DebugAsync("Beep boop. I'm back!");

                await _discord.SetGameAsync("Quality content", streamUrl: "https://www.youtube.com/watch?v=d1YBv2mWll0", type: ActivityType.Streaming);
            }
        }

        private TaskCompletionSource<object> _stopTcs;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var tcs = Interlocked.CompareExchange(
                ref _stopTcs,
                new TaskCompletionSource<object>(TaskContinuationOptions.RunContinuationsAsynchronously),
                null);

            if (tcs is null)
            {
                try
                {
                    try
                    {
                        await _discord.StopAsync();
                    }
                    catch { }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        await Logger.Instance.OnShutdownAsync();
                    }
                }
                finally
                {
                    _stopTcs.SetResult(null);
                }
            }
            else
            {
                await tcs.Task;
            }
        }
    }
}