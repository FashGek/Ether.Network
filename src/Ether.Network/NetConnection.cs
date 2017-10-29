﻿using Ether.Network.Core;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Ether.Network
{
    /// <summary>
    /// Net connection representing a connection.
    /// </summary>
    public abstract class NetConnection : IDisposable
    {
        /// <summary>
        /// Gets or sets the SendAction.
        /// </summary>
        protected internal Action<NetConnection, byte[]> SendAction { get; set; }

        /// <summary>
        /// Gets the generated unique Id of the connection.
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Gets the connection socket.
        /// </summary>
        public Socket Socket { get; protected set; }

        /// <summary>
        /// Gets the user token.
        /// </summary>
        public IAsyncUserToken Token { get; }
        
        /// <summary>
        /// Creates a new <see cref="NetConnection"/> instance.
        /// </summary>
        protected NetConnection()
        {
            this.Id = Guid.NewGuid();
            this.Token = new AsyncUserToken();
        }
        
        /// <summary>
        /// Initialize this <see cref="NetConnection"/> instance.
        /// </summary>
        /// <param name="socket">Socket</param>
        /// <param name="sendAction">Action to send a buffer through the network.</param>
        internal void Initialize(Socket socket, Action<NetConnection, byte[]> sendAction)
        {
            this.Socket = socket;
            this.SendAction = sendAction;
        }

        /// <summary>
        /// Handle packets.
        /// </summary>
        /// <param name="packet">Packet recieved.</param>
        public abstract void HandleMessage(INetPacketStream packet);

        /// <summary>
        /// Send a packet to this client.
        /// </summary>
        /// <param name="packet"></param>
        public void Send(INetPacketStream packet)
        {
            this.SendAction?.Invoke(this, packet.Buffer);
        }

        /// <summary>
        /// Send a packet to the client passed as parameter.
        /// </summary>
        /// <param name="destClient">Destination client</param>
        /// <param name="packet">Packet to send</param>
        public static void SendTo(NetConnection destClient, INetPacketStream packet)
        {
            destClient.Send(packet);
        }

        /// <summary>
        /// Send to a collection of clients.
        /// </summary>
        /// <param name="clients">Clients</param>
        /// <param name="packet">Packet to send</param>
        public static void SendTo(IEnumerable<NetConnection> clients, INetPacketStream packet)
        {
            foreach (var client in clients)
                client.Send(packet);
        }

        /// <summary>
        /// Dispose the NetConnection resources.
        /// </summary>
        public virtual void Dispose()
        {
            if (this.Socket == null)
                return;

            this.Socket.Shutdown(SocketShutdown.Both);
            this.Socket.Dispose();
            this.Socket = null;
        }
    }
}
