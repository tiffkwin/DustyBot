﻿using Discord.WebSocket;
using DustyBot.Framework.Settings;
using DustyBot.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DustyBot.Settings.LiteDB;
using DustyBot.Helpers;
using DustyBot.Framework.Logging;
using DustyBot.Framework.Utility;
using Discord;

namespace DustyBot.Services
{
    class DaumCafeService : IDisposable, Framework.Services.IService
    {
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

        System.Threading.Timer _timer;

        public ISettingsProvider Settings { get; private set; }
        public DiscordSocketClient Client { get; private set; }
        public ILogger Logger { get; private set; }

        public static readonly TimeSpan UpdateFrequency = TimeSpan.FromMinutes(4);
        bool _updating = false;

        Dictionary<Guid, Tuple<DateTime, DaumCafeSession>> _sessionCache = new Dictionary<Guid, Tuple<DateTime, DaumCafeSession>>();

        public DaumCafeService(DiscordSocketClient client, ISettingsProvider settings, ILogger logger)
        {
            Settings = settings;
            Client = client;
            Logger = logger;
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(OnUpdate, null, (int)UpdateFrequency.TotalMilliseconds, (int)UpdateFrequency.TotalMilliseconds);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        async void OnUpdate(object state)
        {
            await Task.Run(async () =>
            {
                if (_updating)
                    return; //Skip if the previous update is still running

                _updating = true;

                try
                {
                    foreach (var settings in await Settings.Read<MediaSettings>().ConfigureAwait(false))
                    {
                        if (settings.DaumCafeFeeds == null || settings.DaumCafeFeeds.Count <= 0)
                            continue;

                        foreach (var feed in settings.DaumCafeFeeds)
                        {
                            try
                            {
                                await UpdateFeed(feed, settings.ServerId);
                            }
                            catch (Exception ex)
                            {
                                await Logger.Log(new LogMessage(LogSeverity.Error, "Service", $"Failed to update Daum Cafe feed {feed.Id} ({feed.CafeId}/{feed.BoardId}) on server {settings.ServerId}.", ex));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await Logger.Log(new LogMessage(LogSeverity.Error, "Service", "Failed to update Daum Cafe feeds.", ex));
                }
                finally
                {
                    _updating = false;
                }
            });            
        }

        async Task UpdateFeed(DaumCafeFeed feed, ulong serverId)
        {
            var guild = Client.GetGuild(serverId);
            if (guild == null)
                return; //TODO: zombie settings should be cleared

            var channel = guild.GetTextChannel(feed.TargetChannel);
            if (channel == null)
                return; //TODO: zombie settings should be cleared

            //Choose a session
            DaumCafeSession session;
            if (feed.CredentialId != Guid.Empty)
            {
                Tuple<DateTime, DaumCafeSession> dateSession;
                if (!_sessionCache.TryGetValue(feed.CredentialId, out dateSession) || DateTime.Now - dateSession.Item1 > SessionLifetime)
                {
                    var credential = await Modules.CredentialsModule.GetCredential(Settings, feed.CredentialUser, feed.CredentialId);
                    try
                    {
                        session = await DaumCafeSession.Create(credential.Login, credential.Password);
                        _sessionCache[feed.CredentialId] = Tuple.Create(DateTime.Now, session);
                    }
                    catch (Exception ex) when (ex is CountryBlockException || ex is LoginFailedException)
                    {
                        session = DaumCafeSession.Anonymous;
                        _sessionCache[feed.CredentialId] = Tuple.Create(DateTime.Now, session);
                    }
                }
                else
                    session = dateSession.Item2;
            }
            else
                session = DaumCafeSession.Anonymous;

            //Get last post ID
            var lastPostId = await session.GetLastPostId(feed.CafeId, feed.BoardId);

            //If new feed -> just store the last post ID and return
            if (feed.LastPostId < 0)
            {
                await Settings.Modify<MediaSettings>(serverId, s =>
                {
                    var current = s.DaumCafeFeeds.FirstOrDefault(x => x.Id == feed.Id);
                    if (current != null && current.LastPostId < 0)
                        current.LastPostId = lastPostId;
                }).ConfigureAwait(false);

                return;
            }
            
            var currentPostId = feed.LastPostId;
            if (lastPostId <= feed.LastPostId)
                return;

            await Logger.Log(new LogMessage(LogSeverity.Info, "Service", $"Updating feed {feed.CafeId}/{feed.BoardId}" + (lastPostId - currentPostId > 1 ? $", found {lastPostId - currentPostId} new posts ({currentPostId + 1} to {lastPostId})" : $" (post {lastPostId})") + $" on {guild.Name}"));

            while (lastPostId > currentPostId)
            {
                var preview = await CreatePreview(session, feed.CafeId, feed.BoardId, currentPostId + 1);
                
                await channel.SendMessageAsync(preview.Item1.Sanitise(), false, preview.Item2);
                currentPostId++;
            }

            await Settings.Modify<MediaSettings>(serverId, settings =>
            {
                var current = settings.DaumCafeFeeds.FirstOrDefault(x => x.Id == feed.Id);
                if (current != null && current.LastPostId < currentPostId)
                    current.LastPostId = currentPostId;
            }).ConfigureAwait(false);
        }

        private Embed BuildPreview(string title, string url, string description, string imageUrl, string cafeName)
        {
            var embedBuilder = new EmbedBuilder()
                        .WithTitle(title)
                        .WithUrl(url)
                        .WithFooter("Daum Cafe • " + cafeName);

            if (!string.IsNullOrWhiteSpace(description))
                embedBuilder.Description = description.JoinWhiteLines(2).TruncateLines(13, trim: true).Truncate(350);

            if (!string.IsNullOrWhiteSpace(imageUrl) && !imageUrl.Contains("cafe_meta_image.png"))
                embedBuilder.ImageUrl = imageUrl;

            return embedBuilder.Build();
        }

        public async Task<Tuple<string, Embed>> CreatePreview(DaumCafeSession session, string cafeId, string boardId, int postId)
        {
            var mobileUrl = $"http://m.cafe.daum.net/{cafeId}/{boardId}/{postId}";
            var desktopUrl = $"http://cafe.daum.net/{cafeId}/{boardId}/{postId}";

            var text = $"<{desktopUrl}>";
            Embed embed = null;
            try
            {
                var metadata = await session.GetPageMetadata(new Uri(mobileUrl));
                if (metadata.Type == "comment" && (!string.IsNullOrWhiteSpace(metadata.Body.Text) || !string.IsNullOrWhiteSpace(metadata.ImageUrl)))
                {
                    embed = BuildPreview("New memo", mobileUrl, metadata.Body.Text, metadata.Body.ImageUrl, cafeId);
                }
                else if (!string.IsNullOrEmpty(metadata.Body.Subject) && (!string.IsNullOrWhiteSpace(metadata.Body.Text) || !string.IsNullOrWhiteSpace(metadata.ImageUrl)))
                {
                    embed = BuildPreview(metadata.Body.Subject, mobileUrl, metadata.Body.Text, metadata.Body.ImageUrl, cafeId);
                }
                else if (metadata.Type == "article" && !string.IsNullOrWhiteSpace(metadata.Title) && (!string.IsNullOrWhiteSpace(metadata.Description) || !string.IsNullOrWhiteSpace(metadata.ImageUrl)))
                {
                    embed = BuildPreview(metadata.Title, mobileUrl, metadata.Description, metadata.ImageUrl, cafeId);
                }
            }
            catch (Exception ex)
            {
                await Logger.Log(new LogMessage(LogSeverity.Warning, "Service", $"Failed to create Daum Cafe post preview for {mobileUrl}.", ex));
            }

            return Tuple.Create(text, embed);
        }
        
        #region IDisposable 

        private bool _disposed = false;
                
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
                
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
                
                _disposed = true;
            }
        }

        //~()
        //{
        //    Dispose(false);
        //}

        #endregion
    }

}
