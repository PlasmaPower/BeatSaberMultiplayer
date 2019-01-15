﻿using BeatSaberMultiplayer.Data;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace BeatSaberMultiplayer.Misc
{
    public static class ClientHelper
    {
        public static BasePacket ReceiveData(this Socket client, bool waitForData = true)
        {
            if (client == null || !client.Connected)
                return null;

            if (!waitForData && client.Available == 0)
                return null;

            byte[] lengthBuffer = new byte[4];
            client.Receive(lengthBuffer, 0, 4, SocketFlags.None);
            int length = BitConverter.ToInt32(lengthBuffer, 0);

            byte[] dataBuffer = new byte[length];

            int nDataRead = 0;
            int nStartIndex = 0;

            while (nDataRead < length)
            {

                int nBytesRead = client.Receive(dataBuffer, nStartIndex, length - nStartIndex, SocketFlags.None);

                nDataRead += nBytesRead;
                nStartIndex += nBytesRead;
            }

            return new BasePacket(dataBuffer);            
        }

        public static void SendData(this Socket client, BasePacket packet)
        {
            if (client == null || !client.Connected)
                return;

            byte[] buffer = packet.ToBytes();
            StateObject state = new StateObject(buffer.Length) { workSocket = client, packet = packet};
            client.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, SendCallback, state);
        }

        private static void SendCallback(IAsyncResult ar)
        {
            StateObject recState = (StateObject)ar.AsyncState;
            Socket client = recState.workSocket;

            client.EndSend(ar);
            if(recState.packet.commandType == CommandType.Disconnect)
            {
                client.Close();
            }
        }
    }
}