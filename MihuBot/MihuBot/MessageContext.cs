﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot
{
    public class MessageContext
    {
        public readonly DiscordSocketClient Client;

        public readonly SocketMessage Message;
        public readonly string Content;

        public readonly bool IsMentioned;
        public readonly bool IsFromAdmin;

        public MessageContext(DiscordSocketClient client, SocketMessage message)
        {
            Client = client;
            Message = message;
            Content = message.Content.Trim();
            IsMentioned = message.MentionedUsers.Any(u => u.Id == KnownUsers.MihuBot) || message.MentionedRoles.Any(r => r.Name == "MihuBot");
            IsFromAdmin = message.AuthorIsAdmin();
        }

        public ISocketMessageChannel Channel => Message.Channel;
        public SocketGuild Guild => Message.Guild();

        public SocketUser Author => Message.Author;
        public ulong AuthorId => Author.Id;

        public async Task<RestUserMessage> ReplyAsync(string text, bool mention = false)
        {
            if (mention)
                text = MentionUtils.MentionUser(AuthorId) + " " + text;

            return await Channel.SendMessageAsync(text);
        }

        internal async Task DebugAsync(string message)
        {
            lock (Console.Out)
                Console.WriteLine("DEBUG: " + message);

            try
            {
                await Client.GetGuild(566925785563136020ul).GetTextChannel(719903263297896538ul).SendMessageAsync(message);
            }
            catch { }
        }
    }
}