﻿using System;
using System.Net.Sockets;
using Ether.Network.Packets;
using Ether.Network.Client;
using Ether.Network.Core;

namespace Ether.Network.Tests.Context
{
    internal class MyClient : NetClient
    {
        public MyClient(string host, int port, int bufferSize) 
            : base(host, port, bufferSize)
        {
        }

        protected override void HandleMessage(INetPacketStream packet)
        {
            var header = packet.Read<int>();

            switch (header)
            {
                case 0:
                    var message = packet.Read<string>();
                    Console.WriteLine("Received: {0}", message);
                    break;
            }
        }

        protected override void OnConnected()
        {
        }

        protected override void OnDisconnected()
        {
        }

        protected override void OnSocketError(SocketError socketError)
        {
        }
    }
}
