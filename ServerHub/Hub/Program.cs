﻿using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using ServerHub.Misc;
using ServerHub.Rooms;
using System.IO;
using System.Collections.Generic;
using NetTools;
using ServerHub.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static ServerHub.Hub.RCONStructs;
using static ServerHub.Misc.Logger;
using System.Diagnostics;
using System.Text;
using Lidgren.Network;
using System.Text.RegularExpressions;

namespace ServerHub.Hub
{
    public static class Program {
        private static string IP { get; set; }

        public static DateTime serverStartTime;

        public static long networkBytesInNow;
        public static long networkBytesOutNow;

        private static long networkBytesInLast;
        private static long networkBytesOutLast;

        private static DateTime lastNetworkStatsReset;

        public static List<IPAddressRange> blacklistedIPs;
        public static List<ulong> blacklistedIDs;
        public static List<string> blacklistedNames;

        public static List<IPAddressRange> whitelistedIPs;
        public static List<ulong> whitelistedIDs;
        public static List<string> whitelistedNames;

        public static List<IPlugin> plugins;

        public static List<Command> availableCommands = new List<Command>();
        
        static void Main(string[] args) => Start(args);

        static private Thread ListenerThread { get; set; }

#if !DEBUG
        static private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.Exception(e.ExceptionObject.ToString());
            Logger.Instance.Log("Shutting down...");
            List<BaseRoom> rooms = new List<BaseRoom>(RoomsController.GetRoomsList());
            foreach (BaseRoom room in rooms)
            {
                RoomsController.DestroyRoom(room.roomId, "ServerHub exception occured!");
            }
            HubListener.Stop("ServerHub exception occured!");
        }
#endif

        static private void OnShutdown(ShutdownEventArgs obj) {
            foreach (IPlugin plugin in plugins)
            {
                try
                {
                    plugin.ServerShutdown();
                }
                catch (Exception e)
                {
                    Logger.Instance.Warning($"[{plugin.Name}] Exception on ServerShutdown event: {e}");
                }
            }
            HubListener.Stop();
        }

        static void Start(string[] args) {
#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
#endif
            UpdateLists();

            if (!Directory.Exists("Plugins"))
            {
                try
                {
                    Directory.CreateDirectory("Plugins");
                }
                catch (Exception e)
                {
                    Logger.Instance.Exception($"Unable to create Plugins folder! Exception: {e}");
                }
            }

            Logger.Instance.Log($"Beat Saber Multiplayer ServerHub v{Assembly.GetEntryAssembly().GetName().Version}");

            string[] pluginFiles = Directory.GetFiles("Plugins", "*.dll");

            Logger.Instance.Log($"Found {pluginFiles.Length} plugins!");

            plugins = new List<IPlugin>();

            foreach (string path in pluginFiles)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(path));
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.GetInterface("IPlugin") != null)
                        {
                            try
                            {
                                IPlugin pluginInstance = Activator.CreateInstance(t) as IPlugin;

                                plugins.Add(pluginInstance);
                            }
                            catch (Exception e)
                            {
                                Logger.Instance.Error(string.Format("Unable to load plugin {0} in {1}! {2}", t.FullName, Path.GetFileName(path), e));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Instance.Error($"Unable to load assembly {Path.GetFileName(path)}! Exception: {e}");
                }
            }

            foreach(IPlugin plugin in plugins)
            {
                try
                {
                    if (plugin.Init())
                    {
                        Logger.Instance.Log("Initialized plugin: "+plugin.Name+" v"+plugin.Version);
                        HighResolutionTimer.LoopTimer.Elapsed += (object sender, HighResolutionTimerElapsedEventArgs e) =>
                        {
                            try
                            {
                                plugin.Tick(sender, e);
                            }
                            catch (Exception ex)
                            {
                                Logger.Instance.Warning($"[{plugin.Name}] Exception on Tick event: {ex}");
                            }
                        };
                    }
                    else
                    {
                        plugins.Remove(plugin);
                    }
                }
                catch (Exception e)
                {
                    Logger.Instance.Warning($"[{plugin.Name}] Exception on Init event: {e}");
                    plugins.Remove(plugin);
                }
            }

            if (args.Length > 0)
            {
                if (args[0].StartsWith("--"))
                {
                    string comName = args[0].ToLower().TrimStart('-');
                    string[] comArgs = args.Skip(1).ToArray();
                    Logger.Instance.Log(ProcessCommand(comName, comArgs), true);
                }
            }

            ShutdownEventCatcher.Shutdown += OnShutdown;

            if (!Directory.Exists("RoomPresets"))
            {
                try
                {
                    Directory.CreateDirectory("RoomPresets");
                }
                catch (Exception e)
                {
                    Logger.Instance.Exception($"Unable to create RoomPresets folder! Exception: {e}");
                }
            }

            Settings.Instance.Server.Tickrate = Misc.Math.Clamp(Settings.Instance.Server.Tickrate, 5, 150);
            HighResolutionTimer.LoopTimer.Interval = 1000/Settings.Instance.Server.Tickrate;
            HighResolutionTimer.LoopTimer.Elapsed += ProgramLoop;
            HighResolutionTimer.LoopTimer.Start();

            IP = GetPublicIPv4();

            VersionChecker.CheckForUpdates();

            ReadLine.AutoCompletionHandler = new AutoCompletionHandler();
            ReadLine.HistoryEnabled = true;
            SetupCommands();

            Logger.Instance.Log($"Hosting ServerHub @ {IP}:{Settings.Instance.Server.Port}");
            serverStartTime = DateTime.Now;


            ListenerThread = new Thread(HubListener.Start);

            ListenerThread.Start();
            HubListener.Listen = true;

            foreach (IPlugin plugin in plugins)
            {
                try
                {
                    plugin.ServerStart();
                }
                catch (Exception e)
                {
                    Logger.Instance.Warning($"[{plugin.Name}] Exception on ServerStart event: {e}");
                }
            }

            if (Settings.Instance.TournamentMode.Enabled)
                CreateTournamentRooms();

            foreach (string blacklistItem in new string[] { "76561201521433077", "IGGGAMES", "76561199437989403", "VALVE" })
            {
                if (!Settings.Instance.Access.Blacklist.Contains(blacklistItem))
                {
                    Settings.Instance.Access.Blacklist.Add(blacklistItem);
                    Settings.Instance.Save();
                }
            }

            Logger.Instance.Warning($"Use [Help] to display commands");
            Logger.Instance.Warning($"Use [Quit] to exit");


            while (HubListener.Listen)
            {
                var x = ReadLine.Read(">>> ");
                if (x == string.Empty) continue;

                var parsedArgs = ParseLine(x);

                Logger.Instance.Log(ProcessCommand(parsedArgs[0].ToLower(), parsedArgs.Skip(1).ToArray()));
            }
        }

        static void ProgramLoop(object sender, HighResolutionTimerElapsedEventArgs e)
        {
            if(DateTime.Now.Subtract(lastNetworkStatsReset).TotalSeconds > 1f)
            {
                lastNetworkStatsReset = DateTime.Now;
                networkBytesInLast = networkBytesInNow;
                networkBytesInNow = 0;
                networkBytesOutLast = networkBytesOutNow;
                networkBytesOutNow = 0;
            }
        }

        private static void CreateTournamentRooms()
        {
            List<SongInfo> songs = BeatSaver.ConvertSongIDs(Settings.Instance.TournamentMode.SongIDs);

            for (int i = 0; i < Settings.Instance.TournamentMode.Rooms; i++)
            {

                RoomSettings settings = new RoomSettings()
                {
                    Name = string.Format(Settings.Instance.TournamentMode.RoomNameTemplate, i + 1),
                    UsePassword = Settings.Instance.TournamentMode.Password != "",
                    Password = Settings.Instance.TournamentMode.Password,
                    NoFail = true,
                    MaxPlayers = 0,
                    SelectionType = SongSelectionType.Manual,
                    AvailableSongs = songs,
                };

                uint id = RoomsController.CreateRoom(settings);

                Logger.Instance.Log("Created tournament room with ID " + id);
            }
        }

        public static string ProcessCommand(string comName, string[] comArgs)
        {
#if DEBUG
            if(comName == "crash")
            {
                throw new Exception("Debug Exception");
            }
#endif

            try
            {
                if(availableCommands.Any(x => x.name == comName))
                {
                    return availableCommands.First(x => x.name == comName).function(comArgs);
                }

                IPlugin plugin = plugins.FirstOrDefault(x => x.Commands.Any(y => y.name == comName));
                if (plugin != null)
                {
                    Command comm = plugin.Commands.First(x => x.name == comName);
                    return comm.function(comArgs);
                }

                if (!string.IsNullOrEmpty(comName))
                {
                    return $"{comName}: command not found";
                }
                else
                {
                    return $"command not found";
                }
            }
            catch (Exception e)
            {
                return $"Unable to process command! Exception: {e}";
            }
        }

        public static List<string> ParseLine(string line)
        {
            var args = new List<string>();
            var quote = false;
            for (int i = 0, n = 0; i <= line.Length; ++i)
            {
                if ((i == line.Length || line[i] == ' ') && !quote)
                {
                    if (i - n > 0)
                        args.Add(line.Substring(n, i - n).Trim(' ', '"'));

                    n = i + 1;
                    continue;
                }

                if (line[i] == '"')
                    quote = !quote;
            }

            return args;
        }

        private static void SetupCommands()
        {
            #region Console commands
            availableCommands.Add(new Command()
            {
                name = "help",
                help = "help - prints this text",
                function = (comArgs) => {
                    string commands = $"{Environment.NewLine}Default:";
                    foreach (var com in availableCommands.Where(x => !string.IsNullOrEmpty(x.help)))
                    {
                        commands += $"{Environment.NewLine}> {com.help}";
                    }

                    foreach (IPlugin plugin in plugins)
                    {
                        try
                        {
                            commands += $"{Environment.NewLine}{plugin.Name}:";
                            plugin.Commands.ForEach(x => commands += $"{Environment.NewLine}> {x.help}");
                        }
                        catch
                        {

                        }
                    }

                    return $"Commands:{commands}";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "version",
                help = "version - prints versions ofServerHub and installed plugins",
                function = (comArgs) => {
                    string versions = $"ServerHub v{Assembly.GetEntryAssembly().GetName().Version}";
                    foreach (IPlugin plugin in plugins)
                    {
                        try
                        {
                            versions += $"\n{plugin.Name} v{plugin.Version}";
                        }
                        catch
                        {

                        }
                    }
                    return versions;
                }
            });

            availableCommands.Add(new Command()
            {
                name = "quit",
                help = "quit - stops ServerHub",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        Logger.Instance.Log("Shutting down...");
                        List<BaseRoom> rooms = new List<BaseRoom>(RoomsController.GetRoomsList());
                        foreach (BaseRoom room in rooms)
                        {
                            RoomsController.DestroyRoom(room.roomId, "ServerHub is shutting down...");
                        }
                        HubListener.Stop();
                    }

                    Environment.Exit(0);
                    return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "clients",
                help = "clients - prins list of connected clients",
                function = (comArgs) => {
                    string clientsStr = "";
                    if (HubListener.Listen)
                    {
                        clientsStr += $"{Environment.NewLine}┌─Lobby:";
                        if (HubListener.hubClients.Where(x => x.playerConnection != null).Count() == 0)
                        {
                            clientsStr += $"{Environment.NewLine}│ No Clients";
                        }
                        else
                        {
                            List<Client> clients = new List<Client>(HubListener.hubClients.Where(x => x.playerConnection != null));
                            foreach (var client in clients)
                            {
                                IPEndPoint remote = (IPEndPoint)client.playerConnection.RemoteEndPoint;
                                clientsStr += $"{Environment.NewLine}│ [{client.playerConnection.Status} | {client.playerInfo.playerState}] {client.playerInfo.playerName}({client.playerInfo.playerId}) @ {remote.Address}:{remote.Port}";
                            }
                        }

                        if (RadioController.radioStarted)
                        {
                            clientsStr += $"{Environment.NewLine}├─Radio:";
                            if (RadioController.radioClients.Where(x => x.playerConnection != null).Count() == 0)
                            {
                                clientsStr += $"{Environment.NewLine}│ No Clients";
                            }
                            else
                            {
                                List<Client> clients = new List<Client>(RadioController.radioClients.Where(x => x.playerConnection != null));
                                foreach (var client in clients)
                                {
                                    IPEndPoint remote = (IPEndPoint)client.playerConnection.RemoteEndPoint;
                                    clientsStr += $"{Environment.NewLine}│ [{client.playerConnection.Status} | {client.playerInfo.playerState}] {client.playerInfo.playerName}({client.playerInfo.playerId}) @ {remote.Address}:{remote.Port}";
                                }
                            }
                        }

                        if (RoomsController.GetRoomsList().Count > 0)
                        {
                            foreach (var room in RoomsController.GetRoomsList())
                            {
                                clientsStr += $"{Environment.NewLine}├─Room {room.roomId} \"{room.roomSettings.Name}\":";
                                if (room.roomClients.Count == 0)
                                {
                                    clientsStr += $"{Environment.NewLine}│ No Clients";
                                }
                                else
                                {
                                    List<Client> clients = new List<Client>(room.roomClients);
                                    foreach (var client in clients)
                                    {
                                        IPEndPoint remote = (IPEndPoint)client.playerConnection.RemoteEndPoint;
                                        clientsStr += $"{Environment.NewLine}│ [{client.playerConnection.Status} | {client.playerInfo.playerState}] {client.playerInfo.playerName}({client.playerInfo.playerId}) @ {remote.Address}:{remote.Port}";
                                    }
                                }
                            }
                        }

                        clientsStr += $"{Environment.NewLine}└─";

                        return clientsStr;

                    }
                    else
                    return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "blacklist",
                help = "blacklist [add/remove] [nick/playerID/IP] - bans/unbans players with provided player name/SteamID/OculusID/IP",
                function = (comArgs) => {
                    if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                    {
                        switch (comArgs[0])
                        {
                            case "add":
                                {
                                    if (!Settings.Instance.Access.Blacklist.Contains(comArgs[1]))
                                    {
                                        Settings.Instance.Access.Blacklist.Add(comArgs[1]);
                                        Settings.Instance.Save();
                                        UpdateLists();

                                        return $"Successfully banned {comArgs[1]}";
                                    }
                                    else
                                    {
                                        return $"{comArgs[1]} is already blacklisted";
                                    }
                                }
                            case "remove":
                                {
                                    if (Settings.Instance.Access.Blacklist.Remove(comArgs[1]))
                                    {
                                        Settings.Instance.Save();
                                        UpdateLists();
                                        return $"Successfully unbanned {comArgs[1]}";
                                    }
                                    else
                                    {
                                        return $"{comArgs[1]} is not banned";
                                    }
                                }
                            default:
                                {
                                    return $"Command usage: blacklist [add/remove] [nick/playerID/IP]";
                                }
                        }
                    }
                    else
                    {
                        return $"Command usage: blacklist [add/remove] [nick/playerID/IP]";
                    }
                }
            });

            availableCommands.Add(new Command()
            {
                name = "whitelist",
                help = "whitelist [enable/disable/add/remove] [nick/playerID/IP] - enables/disables whitelist and modifies it",
                function = (comArgs) => {
                    if (comArgs.Length >= 1)
                    {
                        switch (comArgs[0])
                        {
                            case "enable":
                                {
                                    Settings.Instance.Access.WhitelistEnabled = true;
                                    Settings.Instance.Save();
                                    UpdateLists();
                                    return $"Whitelist enabled";
                                }
                            case "disable":
                                {
                                    Settings.Instance.Access.WhitelistEnabled = false;
                                    Settings.Instance.Save();
                                    UpdateLists();
                                    return $"Whitelist disabled";
                                }
                            case "add":
                                {
                                    if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                                    {
                                        if (!Settings.Instance.Access.Whitelist.Contains(comArgs[1]))
                                        {
                                            Settings.Instance.Access.Whitelist.Add(comArgs[1]);
                                            Settings.Instance.Save();
                                            UpdateLists();
                                            return $"Successfully whitelisted {comArgs[1]}";
                                        }
                                        else
                                        {
                                            return $"{comArgs[1]} is already whitelisted";
                                        }
                                    }
                                    else
                                    {
                                        return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                    }
                                }
                            case "remove":
                                {
                                    if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                                    {
                                        if (Settings.Instance.Access.Whitelist.Remove(comArgs[1]))
                                        {
                                            Settings.Instance.Save();
                                            UpdateLists();
                                            return $"Successfully removed {comArgs[1]} from whitelist";
                                        }
                                        else
                                        {
                                            return $"{comArgs[1]} is not whitelisted";
                                        }
                                    }
                                    else
                                    {
                                        return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                    }
                                }
                            default:
                                {
                                    return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                }
                        }
                    }
                    else
                    {
                        return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                    }
                }
            });

            availableCommands.Add(new Command()
            {
                name = "tickrate",
                help = "tickrate [5-150] - changes tickrate of ServerHub",
                function = (comArgs) => {
                    int tickrate = Settings.Instance.Server.Tickrate;
                    if (int.TryParse(comArgs[0], out tickrate))
                    {
#if !DEBUG
                                tickrate = Misc.Math.Clamp(tickrate, 5, 150);
#endif
                        Settings.Instance.Server.Tickrate = tickrate;
                        HighResolutionTimer.LoopTimer.Interval = 1000f / tickrate;

                        return $"Set tickrate to {Settings.Instance.Server.Tickrate}";
                    }
                    else
                    {
                        return $"Command usage: tickrate [5-150]";
                    }
                }
            });

            availableCommands.Add(new Command()
            {
                name = "createroom",
                help = "createroom [presetname] - creates new room from provided preset",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        if (comArgs.Length == 1)
                        {
                            string path = comArgs[0];
                            if (!path.EndsWith(".json"))
                            {
                                path += ".json";
                            }

                            if (!path.ToLower().StartsWith("roompresets"))
                            {
                                path = Path.Combine("RoomPresets", path);
                            }

                            if (File.Exists(path))
                            {
                                string json = File.ReadAllText(path);

                                RoomPreset preset = JsonConvert.DeserializeObject<RoomPreset>(json);

                                preset.Update();

                                uint roomId = RoomsController.CreateRoom(preset.GetRoomSettings());

                                return "Created room with ID " + roomId;
                            }
                            else
                            {
                                return $"Unable to create room! File not found: {Path.GetFullPath(path)}";
                            }
                        }
                        else
                        {
                            return $"Command usage: createroom [presetname]";
                        }
                    }
                    else
                        return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "saveroom",
                help = "saveroom [roomId] [presetname] - saves room with provided roomId to the preset",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        if (comArgs.Length == 2)
                        {
                            uint roomId;
                            if (uint.TryParse(comArgs[0], out roomId))
                            {
                                BaseRoom room = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == roomId);
                                if (room != null)
                                {
                                    string path = comArgs[1];
                                    if (!path.EndsWith(".json"))
                                    {
                                        path += ".json";
                                    }

                                    if (!path.ToLower().StartsWith("roompresets"))
                                    {
                                        path = Path.Combine("RoomPresets", path);
                                    }

                                    Logger.Instance.Log("Saving room...");

                                    File.WriteAllText(Path.GetFullPath(path), JsonConvert.SerializeObject(new RoomPreset(room), Formatting.Indented));

                                    return "Saved room with ID " + roomId;
                                }
                                else
                                {
                                    return "Room with ID " + roomId + " not found!";
                                }
                            }
                            else
                            {
                                return $"Command usage: saveroom [roomId] [presetname]";
                            }

                        }
                        else
                        {
                            return $"Command usage: saveroom [roomId] [presetname]";
                        }

                    }
                    else
                        return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "cloneroom",
                help = "cloneroom [roomId] - clones room with provided roomId",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        if (comArgs.Length == 1)
                        {
                            uint roomId;
                            if (uint.TryParse(comArgs[0], out roomId))
                            {
                                BaseRoom room = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == roomId);
                                if (room != null)
                                {
                                    uint newRoomId = RoomsController.CreateRoom(room.roomSettings, room.roomHost);
                                    return "Cloned room roomId is " + newRoomId;
                                }
                                else
                                {
                                    return "Room with ID " + roomId + " not found!";
                                }
                            }
                            else
                            {
                                return $"Command usage: cloneroom [roomId]";
                            }
                        }
                        else
                        {
                            return $"Command usage: cloneroom [roomId]";
                        }
                    }
                    else
                        return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "destroyroom",
                help = "destroyroom [roomId] - destroys room with provided roomId",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        if (comArgs.Length == 1)
                        {
                            uint roomId;
                            if (uint.TryParse(comArgs[0], out roomId))
                            {
                                if (RoomsController.DestroyRoom(roomId))
                                    return "Destroyed room " + roomId;
                                else
                                    return "Room with ID " + roomId + " not found!";
                            }
                            else
                            {
                                return $"Command usage: destroyroom [roomId]";
                            }
                        }
                        else
                        {
                            return $"Command usage: destroyroom [roomId]";
                        }
                    }
                    else
                        return "";
                }
            });
            
            availableCommands.Add(new Command()
            {
                name = "destroyempty",
                help = "destroyempty - destroys empty rooms",
                function = (comArgs) => {
                    string roomsStr = "Destroying empty rooms...";
                    if (HubListener.Listen)
                    {
                        List<uint> emptyRooms = RoomsController.GetRoomsList().Where(x => x.roomClients.Count == 0).Select(y => y.roomId).ToList();
                        if (emptyRooms.Count > 0)
                        {
                            foreach (uint roomId in emptyRooms)
                            {
                                RoomsController.DestroyRoom(roomId);
                                roomsStr += $"\nDestroyed room {roomId}!";
                            }
                        }
                        else
                        {

                            roomsStr += $"\nNo empty rooms!";
                        }
                    }
                    return roomsStr;
                }
            });
            
            availableCommands.Add(new Command()
            {
                name = "message",
                help = "message [roomId or *] [displayTime] [fontSize] [text] - stops ServerHub",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        if (comArgs.Length >= 4)
                        {
                            if (comArgs[0] == "*")
                            {
                                float displayTime;
                                float fontSize;
                                string message = string.Join(" ", comArgs.Skip(3).ToArray()).Replace("\\n", Environment.NewLine);
                                if (float.TryParse(comArgs[1], out displayTime) && float.TryParse(comArgs[2], out fontSize))
                                {
                                    string output = $"Sending message \"{string.Join(" ", comArgs.Skip(3).ToArray())}\" to all rooms...";

                                    List<BaseRoom> rooms = RoomsController.GetRoomsList();
                                    rooms.ForEach((x) =>
                                    {

                                        NetOutgoingMessage outMsg = HubListener.ListenerServer.CreateMessage();
                                        outMsg.Write((byte)CommandType.DisplayMessage);
                                        outMsg.Write(displayTime);
                                        outMsg.Write(fontSize);
                                        outMsg.Write(message);

                                        x.BroadcastPacket(outMsg, NetDeliveryMethod.ReliableOrdered);
                                        x.BroadcastWebSocket(CommandType.DisplayMessage, new DisplayMessage(displayTime, fontSize, message));
                                        output += $"\nSent message to all players in room {x.roomId}!";
                                    });
                                    return output;
                                }
                                else
                                    return $"Command usage: message [roomId or *] [displayTime] [fontSize] [text]";
                            }
                            else
                            {
                                uint roomId;
                                if (uint.TryParse(comArgs[0], out roomId))
                                {
                                    List<BaseRoom> rooms = RoomsController.GetRoomsList();
                                    if (rooms.Any(x => x.roomId == roomId))
                                    {
                                        float displayTime;
                                        float fontSize;
                                        if (float.TryParse(comArgs[1], out displayTime) && float.TryParse(comArgs[2], out fontSize))
                                        {
                                            string message = string.Join(" ", comArgs.Skip(3).ToArray()).Replace("\\n", Environment.NewLine);

                                            BaseRoom room = rooms.First(x => x.roomId == roomId);

                                            NetOutgoingMessage outMsg = HubListener.ListenerServer.CreateMessage();
                                            outMsg.Write((byte)CommandType.DisplayMessage);
                                            outMsg.Write(displayTime);
                                            outMsg.Write(fontSize);
                                            outMsg.Write(message);

                                            room.BroadcastPacket(outMsg, NetDeliveryMethod.ReliableOrdered);
                                            room.BroadcastWebSocket(CommandType.DisplayMessage, new DisplayMessage(displayTime, fontSize, message));
                                            return $"Sent message \"{string.Join(" ", comArgs.Skip(3).ToArray())}\" to all players in room {roomId}!";
                                        }
                                        else
                                            return $"Command usage: message [roomId or *] [displayTime] [fontSize] [text]";
                                    }
                                    else
                                        return "Room with ID " + roomId + " not found!";
                                }
                                else
                                {
                                    return $"Command usage: message [roomId or *] [displayTime] [fontSize] [text]";
                                }
                            }
                        }
                        else
                        {
                            return $"Command usage: message [roomId or *] [displayTime] [fontSize] [text]";
                        }
                    }
                    else
                        return "";
                }
            });
            #endregion

            #region Radio command

            availableCommands.Add(new Command()
            {
                name = "radio",
                help = "radio [help] - controls radio channel",
                function = (comArgs) => {
                    if (HubListener.Listen && comArgs.Length > 0)
                    {
                        switch (comArgs[0].ToLower())
                        {
                            case "help":
                                {
                                    return  "\n> radio help - prints this text" +
                                            "\n> radio enable - enables radio channel in settings and starts it" +
                                            "\n> radio disable - disables radio channel in settings and stops it" +
                                            "\n> radio set [name/iconurl/difficulty] [value] - set specified option to provided value" +
                                            "\n> radio queue list - lists songs in the radio queue" +
                                            "\n> radio queue remove [songName] - removes song with provided name from the radio queue" +
                                            "\n> radio queue add [song key or playlist path/url] - adds song or playlist to the queue";
                                }
                            case "enable":
                                {
                                    if (!Settings.Instance.Radio.EnableRadioChannel)
                                    {
                                        Settings.Instance.Radio.EnableRadioChannel = true;
                                        Settings.Instance.Save();
                                    }
                                    if (!RadioController.radioStarted)
                                    {
                                        RadioController.StartRadioAsync();
                                    }
                                }; break;
                            case "disable":
                                {
                                    if (Settings.Instance.Radio.EnableRadioChannel)
                                    {
                                        Settings.Instance.Radio.EnableRadioChannel = false;
                                        Settings.Instance.Save();
                                    }
                                    if (RadioController.radioStarted)
                                    {
                                        RadioController.StopRadio("Channel disabled from console!");
                                    }
                                }; break;
                            case "set":
                                {
                                    if (comArgs.Length > 2)
                                    {
                                        switch (comArgs[1].ToLower())
                                        {
                                            case "name":
                                                {
                                                    Settings.Instance.Radio.ChannelName = string.Join(' ', comArgs.Skip(2));
                                                    Settings.Instance.Save();

                                                    if (RadioController.radioStarted)
                                                        RadioController.channelInfo.name = Settings.Instance.Radio.ChannelName;

                                                    return $"Channel name set to \"{Settings.Instance.Radio.ChannelName}\"!";
                                                }
                                            case "iconurl":
                                                {
                                                    Settings.Instance.Radio.ChannelIconUrl = string.Join(' ', comArgs.Skip(2));
                                                    Settings.Instance.Save();

                                                    if (RadioController.radioStarted)
                                                        RadioController.channelInfo.iconUrl = Settings.Instance.Radio.ChannelIconUrl;

                                                    return $"Channel icon URL set to \"{Settings.Instance.Radio.ChannelIconUrl}\"!";
                                                }
                                            case "difficulty":
                                                {
                                                    BeatmapDifficulty difficulty = BeatmapDifficulty.Easy;

                                                    if (Enum.TryParse(comArgs[2], true, out difficulty))
                                                    {

                                                        Settings.Instance.Radio.PreferredDifficulty = difficulty;
                                                        Settings.Instance.Save();

                                                        if (RadioController.radioStarted)
                                                            RadioController.channelInfo.preferredDifficulty = Settings.Instance.Radio.PreferredDifficulty;

                                                        return $"Channel preferred difficulty set to \"{Settings.Instance.Radio.PreferredDifficulty}\"!";
                                                    }
                                                    else
                                                        return $"Unable to parse difficulty name!\nAvailable difficulties:\n> Easy\n> Normal\n> Hard\n> Expert\n> ExpertPlus";
                                                }
                                            default:
                                                return $"Unable to parse option name!\nAvaiable options:\n> name\n> iconurl\n> difficulty";
                                        }
                                    }
                                }; break;
                            case "queue":
                                {
                                    if (comArgs.Length > 1)
                                    {
                                        switch (comArgs[1].ToLower())
                                        {
                                            case "list":
                                                {
                                                    if (RadioController.radioStarted)
                                                    {
                                                        string buffer = $"\n┌─Now playing:\n│ {(!string.IsNullOrEmpty(RadioController.channelInfo.currentSong.key) ? RadioController.channelInfo.currentSong.key + ": " : "")} {RadioController.channelInfo.currentSong.songName}";
                                                        buffer += "\n├─Queue:";

                                                        if (RadioController.radioQueue.Count == 0)
                                                        {
                                                            buffer += "\n│ No songs";
                                                        }
                                                        else
                                                        {
                                                            foreach (var song in RadioController.radioQueue)
                                                            {
                                                                buffer += $"\n│ {(!string.IsNullOrEmpty(song.key) ? song.key+": " : "")} {song.songName}";
                                                            }
                                                        }
                                                        buffer += "\n└─";

                                                        return buffer;
                                                    }
                                                    else
                                                    {
                                                        return "Radio channel is stopped!";
                                                    }
                                                }
                                            case "clear":
                                                {
                                                    if (RadioController.radioStarted)
                                                    {
                                                        RadioController.radioQueue.Clear();

                                                        return "Queue cleared!";
                                                    }
                                                    else
                                                    {
                                                        return "Radio channel is stopped!";
                                                    }
                                                }
                                            case "remove":
                                                {
                                                    if (comArgs.Length > 2)
                                                    {
                                                        if (RadioController.radioStarted)
                                                        {
                                                            string arg = string.Join(' ', comArgs.Skip(2));
                                                            SongInfo songInfo = RadioController.radioQueue.FirstOrDefault(x => x.songName.ToLower().Contains(arg.ToLower()) || x.key == arg);
                                                            if (songInfo != null)
                                                            {
                                                                RadioController.radioQueue = new Queue<SongInfo>(RadioController.radioQueue.Where(x => !x.songName.ToLower().Contains(arg.ToLower()) && x.key != arg));
                                                                File.WriteAllText("RadioQueue.json", JsonConvert.SerializeObject(RadioController.radioQueue, Formatting.Indented));
                                                                return $"Successfully removed \"{songInfo.songName}\" from queue!";
                                                            }
                                                            else
                                                            {
                                                                return $"Queue doesn't contain \"{arg}\"!";
                                                            }
                                                        }
                                                        else
                                                        {
                                                            return "Radio channel is stopped!";
                                                        }
                                                    }
                                                }
                                                break;
                                            case "add":
                                                {
                                                    if (comArgs.Length > 2)
                                                    {
                                                        if (RadioController.radioStarted)
                                                        {
                                                            string key = comArgs[2];
                                                            Regex regex = new Regex(@"(\d+-\d+)");
                                                            if (regex.IsMatch(key))
                                                            {
                                                                RadioController.AddSongToQueueByKey(key);
                                                                return "Adding song to the queue...";
                                                            }
                                                            else
                                                            {
                                                                string playlistPath = string.Join(' ', comArgs.Skip(2));

                                                                RadioController.AddPlaylistToQueue(playlistPath);
                                                                return "Adding all songs from playlist to the queue...";
                                                            }
                                                        }
                                                        else
                                                        {
                                                            return "Radio channel is stopped!";
                                                        }
                                                    }
                                                }
                                                break;
                                            default:
                                                return $"Unable to parse option name!\nAvaiable options:\n> list\n> add\n> remove";
                                        }
                                    }
                                };
                                break;

                        }
                    }
                    return $"Command help: radio [help]";
                }
            });

            #endregion

            #region RCON commands
            availableCommands.Add(new Command()
            {
                name = "serverinfo",
                help = "",
                function = (comArgs) => {
                    if (HubListener.Listen)
                    {
                        ServerInfo info = new ServerInfo()
                        {
                            Hostname = IP + ":" + Settings.Instance.Server.Port,
                            Tickrate = (float)System.Math.Round(HubListener.Tickrate, 1),
                            Memory = (float)System.Math.Round((Process.GetCurrentProcess().WorkingSet64 / 1024f / 1024f), 2),
                            Players = HubListener.hubClients.Count + RoomsController.GetRoomsList().Sum(x => x.roomClients.Count),
                            Uptime = (int)DateTime.Now.Subtract(serverStartTime).TotalSeconds,
                            RoomCount = RoomsController.GetRoomsCount(),
                            NetworkIn = (int)networkBytesInLast,
                            NetworkOut = (int)networkBytesOutLast
                        };

                        return JsonConvert.SerializeObject(info);
                    }
                    else return "";
                }
            });
            
            availableCommands.Add(new Command()
            {
                name = "console.tail",
                help = "",
                function = (comArgs) => {
                    int historySize = int.Parse(comArgs[0]);
                    int skip = Misc.Math.Max(Logger.Instance.logHistory.Count - historySize, 0);
                    int take = Misc.Math.Min(historySize, Logger.Instance.logHistory.Count);
                    List<LogMessage> msgs = Logger.Instance.logHistory.Skip(skip).Take(take).ToList();
                    
                    return JsonConvert.SerializeObject(msgs);
                }
            });

            availableCommands.Add(new Command()
            {
                name = "playerlist",
                help = "",
                function = (comArgs) => {

                    if (comArgs.Length == 0)
                    {
                        List<RCONPlayerInfo> hubClients = new List<RCONPlayerInfo>();

                        hubClients.AddRange(HubListener.hubClients.Select(x => new RCONPlayerInfo(x)));

                        return JsonConvert.SerializeObject(new
                        {
                            hubClients
                        });
                    }
                    else if(comArgs.Length == 1)
                    {
                        if(uint.TryParse(comArgs[0], out uint roomId))
                        {
                            if (RoomsController.GetRoomsList().Any(x => x.roomId == roomId))
                            {
                                List<RCONPlayerInfo> roomClients = new List<RCONPlayerInfo>();

                                roomClients = RoomsController.GetRoomsList().First(x => x.roomId == roomId).roomClients.Select(x => new RCONPlayerInfo(x)).ToList();

                                return JsonConvert.SerializeObject(roomClients);
                            }
                            else
                            {
                                return "[]";
                            }
                        }
                        else
                        {
                            return "[]";
                        }
                    }
                    else
                    {
                        return "[]";
                    }
                }
            });

            availableCommands.Add(new Command()
            {
                name = "roomslist",
                help = "",
                function = (comArgs) => {
                    List<RoomInfo> roomInfos = new List<RoomInfo>();
                    roomInfos = RoomsController.GetRoomInfosList();

                    return JsonConvert.SerializeObject(roomInfos);
                }
            });


            availableCommands.Add(new Command()
            {
                name = "radioinfo",
                help = "",
                function = (comArgs) => {
                    SongInfo currentSong = RadioController.channelInfo.currentSong;
                    List<SongInfo> queuedSongs = new List<SongInfo>();
                    List<RCONPlayerInfo> radioClients = new List<RCONPlayerInfo>();

                    queuedSongs.AddRange(RadioController.radioQueue);
                    radioClients.AddRange(RadioController.radioClients.Select(x => new RCONPlayerInfo(x)));

                    return JsonConvert.SerializeObject(new
                    {
                        currentSong,
                        queuedSongs,
                        radioClients
                    });
                }
            });

            availableCommands.Add(new Command()
            {
                name = "getsettings",
                help = "",
                function = (comArgs) => {
                    return JsonConvert.SerializeObject(Settings.Instance);
                }
            });

            availableCommands.Add(new Command()
            {
                name = "setsettings",
                help = "",
                function = (comArgs) => {
                    Settings.Instance.Load(string.Join(' ', comArgs));

                    if (Settings.Instance.Radio.EnableRadioChannel && !RadioController.radioStarted)
                        RadioController.StartRadioAsync();
                    if (!Settings.Instance.Radio.EnableRadioChannel && RadioController.radioStarted)
                        RadioController.StopRadio("Channel disabled from console!");

                    Settings.Instance.Server.Tickrate = Misc.Math.Clamp(Settings.Instance.Server.Tickrate, 5, 150);
                    HighResolutionTimer.LoopTimer.Interval = 1000f / Settings.Instance.Server.Tickrate;

                    UpdateLists();

                    return "";
                }
            });

            availableCommands.Add(new Command()
            {
                name = "accesslist",
                help = "",
                function = (comArgs) => {
                    return JsonConvert.SerializeObject(Settings.Instance.Access);
                }
            });

            #endregion

#if DEBUG
            #region Debug commands
            availableCommands.Add(new Command()
            {
                name = "testroom",
                help = "",
                function = (comArgs) => {
                    uint id = RoomsController.CreateRoom(new RoomSettings() { Name = "Debug Server", UsePassword = true, Password = "test", NoFail = true, MaxPlayers = 4, SelectionType = Data.SongSelectionType.Manual, AvailableSongs = new System.Collections.Generic.List<Data.SongInfo>() { new Data.SongInfo() { levelId = "07da4b5bc7795b08b87888b035760db7".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "451ffd065cf0e6adc01b2c3eda375794".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "97b35d13bac139c089a0c9d9306c9d76".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a0d040d1a4fe833d5f9838d35777d302".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "61e3b11c1a4cd9185db46b1f7bb7ea54".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e2d35a81fc0c54c326b09892c8d5c038".ToUpper(), songName = "" } } }, new Data.PlayerInfo("andruzzzhka", 76561198047255564));
                    return "Created room with ID " + id;
                }
            });
            
            availableCommands.Add(new Command()
            {
                name = "testroomwopass",
                help = "",
                function = (comArgs) => {
                    uint id = RoomsController.CreateRoom(new RoomSettings() { Name = "Debug Server", UsePassword = false, Password = "test", NoFail = false, MaxPlayers = 4, SelectionType = Data.SongSelectionType.Manual, AvailableSongs = new System.Collections.Generic.List<Data.SongInfo>() { new Data.SongInfo() { levelId = "07da4b5bc7795b08b87888b035760db7".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "451ffd065cf0e6adc01b2c3eda375794".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "97b35d13bac139c089a0c9d9306c9d76".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a0d040d1a4fe833d5f9838d35777d302".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "61e3b11c1a4cd9185db46b1f7bb7ea54".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e2d35a81fc0c54c326b09892c8d5c038".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a8f8f95869b90a288a9ce4bdc260fa17".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "7dce2ba59bc69ec59e6ac455b98f3761".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "fbd77e71ce31329e5ebacde40c7401e0".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "7014f67926d216a6e2df026fa67017b0".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "51d0e56ecea0a98637c0323e7a3af7cf".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "9d1e4315971f6644ac94babdbd20e36a".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "9812c675def22f7405e0bf3422134756".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "1d46797ccb24acb86d0403828533df61".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "6ffccb03d75106c5911dd876dfd5f054".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e3a97c826fab2ce5993dc2e71443b9aa".ToUpper(), songName = "" } } }, new Data.PlayerInfo("andruzzzhka", 76561198047255564));
                    return "Created room with ID " + id;
                }
            });
            #endregion
#endif
        }

        static private void UpdateLists()
        {
            IPAddressRange tryIp;
            ulong tryId;

            blacklistedIPs = Settings.Instance.Access.Blacklist.Where(x => IPAddressRange.TryParse(x, out tryIp)).Select(y => IPAddressRange.Parse(y)).ToList();
            blacklistedIDs = Settings.Instance.Access.Blacklist.Where(x => ulong.TryParse(x, out tryId)).Select(y => ulong.Parse(y)).ToList();
            blacklistedNames = Settings.Instance.Access.Blacklist.Where(x => !IPAddressRange.TryParse(x, out tryIp) && !ulong.TryParse(x, out tryId)).ToList();

            whitelistedIPs = Settings.Instance.Access.Whitelist.Where(x => IPAddressRange.TryParse(x, out tryIp)).Select(y => IPAddressRange.Parse(y)).ToList();
            whitelistedIDs = Settings.Instance.Access.Whitelist.Where(x => ulong.TryParse(x, out tryId)).Select(y => ulong.Parse(y)).ToList();
            whitelistedNames = Settings.Instance.Access.Whitelist.Where(x => !IPAddressRange.TryParse(x, out tryIp) && !ulong.TryParse(x, out tryId)).ToList();

            List<Client> clientsToKick = new List<Client>();
            clientsToKick.AddRange(HubListener.hubClients);
            foreach (var room in RoomsController.GetRoomsList())
            {
                clientsToKick.AddRange(room.roomClients);
            }

            foreach (var client in clientsToKick)
            {
                if (Settings.Instance.Access.WhitelistEnabled && !HubListener.IsWhitelisted(client.playerConnection.RemoteEndPoint, client.playerInfo))
                {
                    client.KickClient("You are not whitelisted!");
                }
                if (HubListener.IsBlacklisted(client.playerConnection.RemoteEndPoint, client.playerInfo))
                {
                    client.KickClient("You are banned!");
                }
            }
        }

        public static string GetPublicIPv4() {
            using (var client = new WebClient()) {
                return client.DownloadString("https://api.ipify.org");
            }
        }
    }
}