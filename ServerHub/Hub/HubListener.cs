using Lidgren.Network;
using ServerHub.Data;
using ServerHub.Misc;
using ServerHub.Rooms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerHub.Hub
{
    public enum CommandType : byte { Connect, Disconnect, GetRooms, CreateRoom, JoinRoom, GetRoomInfo, LeaveRoom, DestroyRoom, TransferHost, SetSelectedSong, StartLevel, UpdatePlayerInfo, PlayerReady, SetGameState, DisplayMessage, SendEventMessage, GetChannelInfo, JoinChannel, GetSongDuration }

    public static class HubListener
    {
        public static event Action<Client> ClientConnected;

        public static event Action<Client, string, string> EventMessageReceived;

        private static List<float> _ticksLength = new List<float>();
        private static DateTime _lastTick;

        public static float Tickrate {
            get {
                return (1000 / (_ticksLength.Average()));
            }
        }

        public static Timer pingTimer;

        public static NetServer ListenerServer;

        static public bool Listen;

        public static List<Client> hubClients = new List<Client>();

        private static string _currentTitle;

        public static void Start()
        {
            if (Settings.Instance.Server.EnableWebSocketServer)
            {
                WebSocketListener.Start();
            }

            pingTimer = new Timer(PingTimerCallback, null, 7500, 10000);
            HighResolutionTimer.LoopTimer.Elapsed += HubLoop;
            HighResolutionTimer.LoopTimer.AfterElapsed += (sender, e) => 
            {
                if (ListenerServer != null)
                {
                    ListenerServer.FlushSendQueue();
                }
            };
            _lastTick = DateTime.Now;

            NetPeerConfiguration Config = new NetPeerConfiguration("BeatSaberMultiplayer")
            {
                Port = Settings.Instance.Server.Port,
                EnableUPnP = Settings.Instance.Server.TryUPnP,
                AutoFlushSendQueue = false
            };
            Config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            
            ListenerServer = new NetServer(Config);
            ListenerServer.Start();

            if (Settings.Instance.Radio.EnableRadioChannel)
            {
                RadioController.StartRadioAsync();
            }

            if (Settings.Instance.Server.TryUPnP)
            {
                ListenerServer.UPnP.ForwardPort(Config.Port, "Beat Saber Multiplayer ServerHub");
            }
        }

        public static void Stop(string reason = "")
        {
            ListenerServer.Shutdown(string.IsNullOrEmpty(reason) ? "Server is shutting down..." : reason);
            Listen = false;
            WebSocketListener.Stop();
            hubClients.ForEach(x => x.KickClient(string.IsNullOrEmpty(reason) ? "Server is shutting down..." : reason));
            RadioController.StopRadio(string.IsNullOrEmpty(reason) ? "Server is shutting down..." : reason);
        }

        private static void HubLoop(object sender, HighResolutionTimerElapsedEventArgs e)
        {
            if(_ticksLength.Count > 30)
            {
                _ticksLength.RemoveAt(0);
            }
            _ticksLength.Add(DateTime.UtcNow.Subtract(_lastTick).Ticks/TimeSpan.TicksPerMillisecond);
            _lastTick = DateTime.UtcNow;
            List<RoomInfo> roomsList = RoomsController.GetRoomInfosList();

            string titleBuffer = $"ServerHub v{Assembly.GetEntryAssembly().GetName().Version}: {roomsList.Count} rooms, {hubClients.Count} clients in lobby, {roomsList.Select(x => x.players).Sum() + hubClients.Count} clients total {(Settings.Instance.Server.ShowTickrateInTitle ? $", {Tickrate.ToString("0.0")} tickrate" : "")}";

            if (_currentTitle != titleBuffer)
            {
                _currentTitle = titleBuffer;
                Console.Title = _currentTitle;
            }


            NetIncomingMessage msg;
            while ((msg = ListenerServer.ReadMessage()) != null)
            {
                try
                {
                    Program.networkBytesInNow += msg.LengthBytes;

                    switch (msg.MessageType)
                    {

                        case NetIncomingMessageType.ConnectionApproval:
                            {
                                uint version = msg.ReadUInt32();
                                uint serverVersion = ((uint)Assembly.GetEntryAssembly().GetName().Version.Major).ConcatUInts((uint)Assembly.GetEntryAssembly().GetName().Version.Minor).ConcatUInts((uint)Assembly.GetEntryAssembly().GetName().Version.Build).ConcatUInts((uint)Assembly.GetEntryAssembly().GetName().Version.Revision);

                                if (CompareVersions(version, serverVersion))
                                {
                                    msg.SenderConnection.Deny($"Version mismatch!\nServer:{serverVersion}\nClient:{version}");
                                    Logger.Instance.Log($"Client version v{version} tried to connect");
                                    break;
                                }

                                PlayerInfo playerInfo = new PlayerInfo(msg);

                                if (Settings.Instance.Access.WhitelistEnabled)
                                {
                                    if (!IsWhitelisted(msg.SenderConnection.RemoteEndPoint, playerInfo))
                                    {
                                        msg.SenderConnection.Deny("You are not whitelisted on this ServerHub!");
                                        Logger.Instance.Warning($"Client {playerInfo.playerName}({playerInfo.playerId})@{msg.SenderConnection.RemoteEndPoint.Address} is not whitelisted!");
                                        break;
                                    }
                                }

                                if (IsBlacklisted(msg.SenderConnection.RemoteEndPoint, playerInfo))
                                {
                                    msg.SenderConnection.Deny("You are banned on this ServerHub!");
                                    Logger.Instance.Warning($"Client {playerInfo.playerName}({playerInfo.playerId})@{msg.SenderConnection.RemoteEndPoint.Address} is banned!");
                                    break;
                                }

                                msg.SenderConnection.Approve();

                                Client client = new Client(msg.SenderConnection, playerInfo);
                                client.playerInfo.playerState = PlayerState.Lobby;

                                client.ClientDisconnected += ClientDisconnected;

                                hubClients.Add(client);
                                Logger.Instance.Log($"{playerInfo.playerName} connected!");
                            };
                            break;
                        case NetIncomingMessageType.Data:
                            {
                                Client client = hubClients.Concat(RoomsController.GetRoomsList().SelectMany(x => x.roomClients)).FirstOrDefault(x => x.playerConnection.RemoteEndPoint == msg.SenderEndPoint);

                                switch ((CommandType)msg.ReadByte())
                                {
                                    case CommandType.Disconnect:
                                        {
                                            msg.SenderConnection.Disconnect("Disconnected");
                                        }
                                        break;
                                    case CommandType.UpdatePlayerInfo:
                                        {
                                            if (client != null)
                                            {
                                                client.playerInfo = new PlayerInfo(msg);
                                            }
                                        }
                                        break;
                                    case CommandType.JoinRoom:
                                        {
                                            if (client != null)
                                            {
                                                uint roomId = msg.ReadUInt32();

                                                BaseRoom room = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == roomId);

                                                if (room != null)
                                                {
                                                    if (room.roomSettings.UsePassword)
                                                    {
                                                        if (RoomsController.ClientJoined(client, roomId, msg.ReadString()))
                                                        {
                                                            if (hubClients.Contains(client))
                                                                hubClients.Remove(client);
                                                            client.joinedRoomID = roomId;
                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (RoomsController.ClientJoined(client, roomId, ""))
                                                        {
                                                            if (hubClients.Contains(client))
                                                                hubClients.Remove(client);
                                                            client.joinedRoomID = roomId;
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    RoomsController.ClientJoined(client, roomId, "");
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.LeaveRoom:
                                        {
                                            if (client != null)
                                            {
                                                RoomsController.ClientLeftRoom(client);
                                                client.joinedRoomID = 0;
                                                client.playerInfo.playerState = PlayerState.Lobby;
                                                if (!hubClients.Contains(client))
                                                    hubClients.Add(client);
                                            }
                                        }
                                        break;
                                    case CommandType.GetRooms:
                                        {
                                            NetOutgoingMessage outMsg = ListenerServer.CreateMessage();
                                            outMsg.Write((byte)CommandType.GetRooms);
                                            RoomsController.AddRoomListToMessage(outMsg);

                                            msg.SenderConnection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                                            Program.networkBytesOutNow += outMsg.LengthBytes;
                                        }
                                        break;
                                    case CommandType.CreateRoom:
                                        {
                                            if (client != null)
                                            {
                                                uint roomId = RoomsController.CreateRoom(new RoomSettings(msg), client.playerInfo);

                                                NetOutgoingMessage outMsg = ListenerServer.CreateMessage(5);
                                                outMsg.Write((byte)CommandType.CreateRoom);
                                                outMsg.Write(roomId);

                                                msg.SenderConnection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                                                Program.networkBytesOutNow += outMsg.LengthBytes;
                                            }
                                        }
                                        break;
                                    case CommandType.GetRoomInfo:
                                        {
                                            if (client != null)
                                            {
#if DEBUG
                                                Logger.Instance.Log("GetRoomInfo: Client room=" + client.joinedRoomID);
#endif
                                                if (client.joinedRoomID != 0)
                                                {
                                                    BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                    if (joinedRoom != null)
                                                    {
#if DEBUG
                                                        Logger.Instance.Log("Room exists!");
#endif
                                                        NetOutgoingMessage outMsg = ListenerServer.CreateMessage();

                                                        outMsg.Write((byte)CommandType.GetRoomInfo);

                                                        bool includeSongList = (msg.ReadByte() == 1);

                                                        if (includeSongList)
                                                        {
                                                            outMsg.Write((byte)1);
                                                            joinedRoom.AddSongInfosToMessage(outMsg);
                                                        }
                                                        else
                                                        {
                                                            outMsg.Write((byte)0);
                                                        }

                                                        joinedRoom.GetRoomInfo().AddToMessage(outMsg);

                                                        msg.SenderConnection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                                                        Program.networkBytesOutNow += outMsg.LengthBytes;
                                                    }
                                                }
                                            }

                                        }
                                        break;
                                    case CommandType.SetSelectedSong:
                                        {
                                            if (client != null && client.joinedRoomID != 0)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    if (msg.LengthBytes < 16)
                                                    {
                                                        joinedRoom.SetSelectedSong(client.playerInfo, null);
                                                    }
                                                    else
                                                    {
                                                        joinedRoom.SetSelectedSong(client.playerInfo, new SongInfo(msg));
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.StartLevel:
                                        {
#if DEBUG
                                            Logger.Instance.Log("Received command StartLevel");
#endif

                                            if (client != null && client.joinedRoomID != 0)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    byte difficulty = msg.ReadByte();
                                                    SongInfo song = new SongInfo(msg);
                                                    song.songDuration += 2.5f;
                                                    joinedRoom.StartLevel(client.playerInfo, difficulty, song);
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.DestroyRoom:
                                        {
                                            if (client != null && client.joinedRoomID != 0)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    joinedRoom.DestroyRoom(client.playerInfo);
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.TransferHost:
                                        {
                                            if (client != null && client.joinedRoomID != 0)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    joinedRoom.TransferHost(client.playerInfo, new PlayerInfo(msg));
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.PlayerReady:
                                        {
                                            if (client != null && client.joinedRoomID != 0)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    joinedRoom.ReadyStateChanged(client.playerInfo, msg.ReadBoolean());
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.SendEventMessage:
                                        {
                                            if (client != null && client.joinedRoomID != 0 && Settings.Instance.Server.AllowEventMessages)
                                            {
                                                BaseRoom joinedRoom = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == client.joinedRoomID);
                                                if (joinedRoom != null)
                                                {
                                                    string header = msg.ReadString();
                                                    string data = msg.ReadString();

                                                    joinedRoom.BroadcastEventMessage(header, data, new List<Client>() { client });
                                                    joinedRoom.BroadcastWebSocket(CommandType.SendEventMessage, new EventMessage(header, data));

                                                    EventMessageReceived?.Invoke(client, header, data);
#if DEBUG
                                                    Logger.Instance.Log($"Received event message! Header=\"{header}\", Data=\"{data}\"");
#endif
                                                }
                                            }
                                        }
                                        break;
                                    case CommandType.GetChannelInfo:
                                        {
                                            if (Settings.Instance.Radio.EnableRadioChannel)
                                            {
                                                NetOutgoingMessage outMsg = ListenerServer.CreateMessage();
                                                outMsg.Write((byte)CommandType.GetChannelInfo);
                                                RadioController.channelInfo.AddToMessage(outMsg);

                                                msg.SenderConnection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                                                Program.networkBytesOutNow += outMsg.LengthBytes;
                                            }
                                        }
                                        break;
                                    case CommandType.JoinChannel:
                                        {
                                            NetOutgoingMessage outMsg = ListenerServer.CreateMessage();
                                            outMsg.Write((byte)CommandType.JoinChannel);
                                            if (RadioController.ClientJoinedChannel(client))
                                            {
                                                outMsg.Write((byte)0);
                                                hubClients.Remove(client);
                                            }
                                            else
                                            {
                                                outMsg.Write((byte)1);
                                            }

                                            msg.SenderConnection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                                            Program.networkBytesOutNow += outMsg.LengthBytes;
                                        }
                                        break;
                                    case CommandType.GetSongDuration:
                                        {
                                            if (RadioController.radioClients.Contains(client) && RadioController.requestingSongDuration)
                                            {
                                                SongInfo info = new SongInfo(msg);
                                                if(info.levelId == RadioController.channelInfo.currentSong.levelId)
                                                    RadioController.songDurationResponses.TryAdd(client, info.songDuration);
                                            }
                                        }
                                        break;
                                }
                            };
                            break;



                        case NetIncomingMessageType.WarningMessage:
                            Logger.Instance.Warning(msg.ReadString());
                            break;
                        case NetIncomingMessageType.ErrorMessage:
                            Logger.Instance.Error(msg.ReadString());
                            break;
                        case NetIncomingMessageType.StatusChanged:
                            {
                                NetConnectionStatus status = (NetConnectionStatus)msg.ReadByte();

                                Client client = hubClients.FirstOrDefault(x => x.playerConnection.RemoteEndPoint == msg.SenderEndPoint);

                                if (client != null)
                                {
                                    if (status == NetConnectionStatus.Disconnected)
                                    {
                                        ClientDisconnected(client);
                                    }
                                }
                            }
                            break;
#if DEBUG
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                            Logger.Instance.Log(msg.ReadString());
                            break;
                        default:
                            Logger.Instance.Log("Unhandled message type: " + msg.MessageType);
                            break;
#endif
                    }
                }catch(Exception ex)
                {
                    Logger.Instance.Log($"Exception on message received: {ex}");
                }
                ListenerServer.Recycle(msg);
            }


        }

        private static bool CompareVersions(uint clientVersion, uint serverVersion)
        {
            string client = clientVersion.ToString();
            string server = serverVersion.ToString();

            return (client.Substring(0, client.Length - 1)) != (server.Substring(0, server.Length - 1));
        }

        public static bool IsBlacklisted(IPEndPoint ip, PlayerInfo playerInfo)
        {
            return Program.blacklistedIPs.Any(x => x.Contains(ip.Address)) ||
                    Program.blacklistedIDs.Contains(playerInfo.playerId) ||
                    Program.blacklistedNames.Contains(playerInfo.playerName);
        }

        public static bool IsWhitelisted(IPEndPoint ip, PlayerInfo playerInfo)
        {
            return Program.whitelistedIPs.Any(x => x.Contains(ip.Address)) ||
                    Program.whitelistedIDs.Contains(playerInfo.playerId) ||
                    Program.whitelistedNames.Contains(playerInfo.playerName);
        }

        private static void PingTimerCallback(object state)
        {
            try
            {
                foreach(Client client in hubClients.Concat(RoomsController.GetRoomsList().SelectMany(x => x.roomClients)).Concat(RadioController.radioClients))
                {
                    if(client.playerConnection.Status == NetConnectionStatus.Disconnected)
                    {
                        ClientDisconnected(client);
                    }
                }

                List<uint> emptyRooms = RoomsController.GetRoomsList().Where(x => x.roomClients.Count == 0 && !x.noHost).Select(y => y.roomId).ToList();
                if (emptyRooms.Count > 0 && !Settings.Instance.TournamentMode.Enabled)
                {
                    Logger.Instance.Log("Destroying empty rooms...");
                    foreach (uint roomId in emptyRooms)
                    {
                        RoomsController.DestroyRoom(roomId);
                        Logger.Instance.Log($"Destroyed room {roomId}!");
                    }
                }

            }
            catch (Exception e)
            {
#if DEBUG
                Logger.Instance.Warning("PingTimerCallback Exception: "+e);
#endif
            }
        }

        private static void ClientDisconnected(Client sender)
        {
            RoomsController.ClientLeftRoom(sender);
            RadioController.ClientLeftChannel(sender);
            if(hubClients.Contains(sender))
                hubClients.Remove(sender);
            sender.ClientDisconnected -= ClientDisconnected;
        }

    }
}