﻿using ServerHub.Data;
using ServerHub.Rooms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerHub.Hub
{
    public class RCONStructs
    {

        public struct OutgoingMessage
        {
            public string Message { get; set; }
            public int Identifier { get; set; }
            public string Type { get; set; }
            public string Stacktrace { get; set; }
        }

        public struct IncomingMessage
        {
            public int Identifier { get; set; }
            public string Message { get; set; }
            public string Name { get; set; }
        }

        public struct ServerInfo
        {
            public string Hostname { get; set; }
            public int Players { get; set; }
            public int RoomCount { get; set; }
            public int Uptime { get; set; }
            public float Tickrate { get; set; }
            public float Memory { get; set; }
            public int NetworkIn { get; set; }
            public int NetworkOut { get; set; }
        }

        public struct RCONPlayerInfo
        {
            public string PlayerID;
            public string DisplayName;
            public string State;
            public string Address;
            public float ConnectedSeconds;

            public RCONPlayerInfo(Client client)
            {
                PlayerID = client.playerInfo.playerIdString;
                DisplayName = client.playerInfo.playerName;
                State = client.playerInfo.playerState.ToString();
                Address = client.playerConnection.RemoteEndPoint.ToString();
                ConnectedSeconds = (float)DateTime.Now.Subtract(client.joinTime).TotalSeconds;
            }
        }
    }
}