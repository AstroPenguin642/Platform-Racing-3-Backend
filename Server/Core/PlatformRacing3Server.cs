﻿using log4net;
using Newtonsoft.Json;
using Platform_Racing_3_Common.Campaign;
using Platform_Racing_3_Common.Database;
using Platform_Racing_3_Common.Redis;
using Platform_Racing_3_Common.Server;
using Platform_Racing_3_Common.User;
using Platform_Racing_3_Server.Config;
using Platform_Racing_3_Server.Game.Chat;
using Platform_Racing_3_Server.Game.Client;
using Platform_Racing_3_Server.Game.Commands;
using Platform_Racing_3_Server.Game.Lobby;
using Platform_Racing_3_Server.Game.Match;
using Platform_Racing_3_Server.Net;
using Platform_Racing_3_Server.Net.TCP;
using Platform_Racing_3_Server_API.Core;
using Renci.SshNet;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Platform_Racing_3_Server.Core
{
    internal sealed class PlatformRacing3Server : PlatformRacing3
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const uint PROTOCOL_VERSION = 24;

        private static Stopwatch StartTime { get; set; }

        public static ServerConfig ServerConfig { get; set; }

        public static ServerManager ServerManager { get; private set; }
        public static CommandManager CommandManager { get; private set; }
        public static ChatRoomManager ChatRoomManager { get; private set; }
        public static MatchListingManager MatchListingManager { get; private set; }
        public static MatchManager MatchManager { get; private set; }

        public static CampaignManager CampaignManager { get; private set; }

        public static ClientManager ClientManager { get; private set; }
        public static NetworkManager NetworkManager { get; private set; }

        public static Timer StatusTimer { get; private set; }

        internal void Init()
        {
            PlatformRacing3Server.Logger.Info("Starting up server...");

            try
            {
                PlatformRacing3Server.StartTime = Stopwatch.StartNew();

                PlatformRacing3Server.ServerConfig = JsonConvert.DeserializeObject<ServerConfig>(File.ReadAllText("settings.json"));

                RedisConnection.Init(PlatformRacing3Server.ServerConfig);
                DatabaseConnection.Init(PlatformRacing3Server.ServerConfig);

                PlatformRacing3Server.ServerManager = new ServerManager();
                PlatformRacing3Server.ServerManager.LoadServersAsync().Wait();
                PlatformRacing3Server.CommandManager = new CommandManager();
                PlatformRacing3Server.ChatRoomManager = new ChatRoomManager();
                PlatformRacing3Server.MatchListingManager = new MatchListingManager();
                PlatformRacing3Server.MatchManager = new MatchManager();

                PlatformRacing3Server.CampaignManager = new CampaignManager();
                PlatformRacing3Server.CampaignManager.LoadCampaignTimesAsync().Wait();
                PlatformRacing3Server.CampaignManager.LoadPrizesAsync().Wait();

                PlatformRacing3Server.ClientManager = new ClientManager();
                PlatformRacing3Server.NetworkManager = new NetworkManager();
                PlatformRacing3Server.NetworkManager.AddListener(new NetworkListenerTCP(new IPEndPoint(IPAddress.Parse(PlatformRacing3Server.ServerConfig.BindIp), PlatformRacing3Server.ServerConfig.BindPort), 100), true);

                PlatformRacing3Server.StatusTimer = new Timer(this.UpdateStatus, null, 1000, 1000);
            }
            catch(Exception ex)
            {
                PlatformRacing3Server.Logger.Fatal("Failed to boot", ex);

                this.FailedToBoot();
            }
        }

        private void UpdateStatus(object state)
        {
            //Kinda look bulky but two of them is requrired
            RedisConnection.GetDatabase().StringSetAsync($"server-status:{PlatformRacing3Server.ServerConfig.ServerId}", $"{PlatformRacing3Server.ClientManager.Count} online", TimeSpan.FromSeconds(3), When.Always, CommandFlags.FireAndForget);
            RedisConnection.GetDatabase().PublishAsync("ServerStatusUpdated", $"{PlatformRacing3Server.ServerConfig.ServerId}\0{PlatformRacing3Server.ClientManager.Count} online");
        }

        private void FailedToBoot()
        {
            Console.WriteLine("Press any key to exit... ");
            Console.ReadKey();

            Environment.Exit(0);
        }

        public void Shutdown()
        {
            PlatformRacing3Server.NetworkManager.Shutdown();

            Environment.Exit(0);
        }
        
        public static TimeSpan Uptime => PlatformRacing3Server.StartTime.Elapsed;
    }
}
