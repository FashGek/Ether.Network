﻿using Ether.Network.Exceptions;
using Ether.Network.Packets;
using Ether.Network.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Ether.Network
{
    /// <summary>
    /// Managed TCP socket server.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class NetServer<T> : INetServer, IDisposable where T : NetConnection, new()
    {
        private readonly ConcurrentDictionary<Guid, T> _clients;
        private readonly ManualResetEvent _resetEvent;
        private readonly SocketAsyncEventArgs _acceptArgs;

        private bool _isRunning;
        private BufferManager _bufferManager;
        private SocketAsyncEventArgsPool _readPool;
        private SocketAsyncEventArgsPool _writePool;

        /// <summary>
        /// Gets the <see cref="NetServer{T}"/> listening socket.
        /// </summary>
        protected Socket Socket { get; private set; }

        /// <summary>
        /// Gets the <see cref="NetServer{T}"/> configuration
        /// </summary>
        protected NetServerConfiguration Configuration { get; private set; }

        /// <summary>
        /// Gets the <see cref="NetServer{T}"/> running state.
        /// </summary>
        public bool IsRunning => this._isRunning;
        
        /// <summary>
        /// Gets the connected client.
        /// </summary>
        public IReadOnlyCollection<T> Clients => this._clients.Values as IReadOnlyCollection<T>;

        /// <summary>
        /// Creates a new <see cref="NetServer{T}"/> instance.
        /// </summary>
        protected NetServer()
        {
            this.Configuration = new NetServerConfiguration(this);
            this.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this._clients = new ConcurrentDictionary<Guid, T>();
            this._resetEvent = new ManualResetEvent(false);
            this._isRunning = false;
            this._acceptArgs = new SocketAsyncEventArgs();
            this._acceptArgs.Completed += this.IO_Completed;
        }

        /// <summary>
        /// Initialize and start the server.
        /// </summary>
        public void Start()
        {
            if (this._isRunning)
                throw new InvalidOperationException("Server is already running.");
            
            if (this.Configuration.Port <= 0)
                throw new EtherConfigurationException($"{this.Configuration.Port} is not a valid port.");

            var address = this.Configuration.Address;
            if (address == null)
                throw new EtherConfigurationException($"Invalid host : {this.Configuration.Host}");
            
            this._bufferManager = new BufferManager(this.Configuration.MaximumNumberOfConnections, this.Configuration.BufferSize);

            if (this._readPool == null)
                this._readPool = new SocketAsyncEventArgsPool(this.Configuration.MaximumNumberOfConnections);

            if (this._writePool == null)
                this._writePool = new SocketAsyncEventArgsPool(this.Configuration.MaximumNumberOfConnections);

            this.Initialize();
            this.Socket.Bind(new IPEndPoint(address, this.Configuration.Port));
            this.Socket.Listen(this.Configuration.Backlog);
            this.StartAccept();

            this._isRunning = true;
            this._resetEvent.WaitOne();
        }

        /// <summary>
        /// Stop the server.
        /// </summary>
        public void Stop()
        {
            if (this._isRunning)
            {
                this._isRunning = false;
                this._resetEvent.Set();
                this.Socket.Shutdown(SocketShutdown.Both);
            }
        }

        /// <summary>
        /// Disconnects the client from this server.
        /// </summary>
        /// <param name="clientId">Client unique Id</param>
        public void DisconnectClient(Guid clientId)
        {
            if (!this._clients.ContainsKey(clientId))
                throw new EtherClientNotFoundException(clientId);

            if (this._clients.TryRemove(clientId, out T removedClient))
            {
                removedClient.Dispose();
                this.OnClientDisconnected(removedClient);
            }
        }

        /// <summary>
        /// Initialize the server resourrces.
        /// </summary>
        protected abstract void Initialize();

        /// <summary>
        /// Triggered when a new client is connected to the server.
        /// </summary>
        /// <param name="connection"></param>
        protected abstract void OnClientConnected(T connection);

        /// <summary>
        /// Triggered when a client disconnects from the server.
        /// </summary>
        /// <param name="connection"></param>
        protected abstract void OnClientDisconnected(T connection);

        /// <summary>
        /// Split an incoming network buffer.
        /// </summary>
        /// <param name="buffer">Incoming data buffer</param>
        /// <returns>Readonly collection of <see cref="NetPacketBase"/></returns>
        protected virtual IReadOnlyCollection<NetPacketBase> SplitPackets(byte[] buffer)
        {
            return NetPacket.Split(buffer);
        }

        /// <summary>
        /// Starts the accept connection async operation.
        /// </summary>
        private void StartAccept()
        {
            if (this._acceptArgs.AcceptSocket != null)
                this._acceptArgs.AcceptSocket = null;
            if (!this.Socket.AcceptAsync(this._acceptArgs))
                this.ProcessAccept(this._acceptArgs);
        }

        /// <summary>
        /// Process the accept connection async operation.
        /// </summary>
        /// <param name="e"></param>
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var client = new T();
                client.Initialize(e.AcceptSocket, this.SendData);

                if (!this._clients.TryAdd(client.Id, client))
                    throw new EtherException($"Client {client.Id} already exists in client list.");

                this.OnClientConnected(client);
                
                SocketAsyncEventArgs readArgs = this._readPool.Pop();
                readArgs.UserToken = client;
                readArgs.Completed += this.IO_Completed;
                this._bufferManager.SetBuffer(readArgs);

                if (e.AcceptSocket != null && !e.AcceptSocket.ReceiveAsync(readArgs))
                    this.ProcessReceive(readArgs);
            }

            e.AcceptSocket = null;
            this.StartAccept();
        }

        /// <summary>
        /// Process the receive async operation on one <see cref="SocketAsyncEventArgs"/>.
        /// </summary>
        /// <param name="e"></param>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                var netConnection = e.UserToken as NetConnection;

                this.DispatchPackets(netConnection, e);
                if (netConnection.Socket != null && !netConnection.Socket.ReceiveAsync(e))
                    this.ProcessReceive(e);
            }
            else
            {
                var netConnection = e.UserToken as T;

                this.DisconnectClient(netConnection.Id);
                this.OnClientDisconnected(netConnection);
                e.UserToken = null;
                e.Completed -= this.IO_Completed;
                this._bufferManager.FreeBuffer(e);
                this._readPool.Push(e);
            }
        }

        /// <summary>
        /// Split and dispatch incoming packets to the <see cref="NetConnection"/>.
        /// </summary>
        /// <param name="netConnection"></param>
        /// <param name="e"></param>
        private void DispatchPackets(NetConnection netConnection, SocketAsyncEventArgs e)
        {
            byte[] buffer = NetUtils.GetPacketBuffer(e.Buffer, e.Offset, e.BytesTransferred);
            Console.WriteLine("Bytes transfered: " + e.BytesTransferred);
            IReadOnlyCollection<NetPacketBase> packets = this.SplitPackets(buffer);


            foreach (var packet in packets)
            {
                try
                {
                    netConnection.HandleMessage(packet);
                }
                catch
                {

                }
            }
        }

        /// <summary>
        /// Send data to a <see cref="NetConnection"/>.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="buffer"></param>
        private void SendData(NetConnection sender, byte[] buffer)
        {
            SocketAsyncEventArgs sendArg = this._writePool.Pop();
            sendArg.UserToken = sender;
            sendArg.Completed += this.IO_Completed;
            sendArg.SetBuffer(buffer, 0, buffer.Length);

            if (sender.Socket != null && !sender.Socket.SendAsync(sendArg))
                this.ProcessSend(sendArg);
        }

        /// <summary>
        /// Process the send async operation.
        /// </summary>
        /// <param name="e"></param>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            var netConnection = e.UserToken as NetConnection;
            bool resetBuffer = true;

            if (e.SocketError == SocketError.Success)
            {
                if (e.BytesTransferred < e.Buffer.Length)
                {
                    resetBuffer = false;
                    e.SetBuffer(e.BytesTransferred, e.Buffer.Length - e.BytesTransferred);
                    if (netConnection.Socket != null && !netConnection.Socket.SendAsync(e))
                        this.ProcessSend(e);
                }
            }
            else
            {
                this.DisconnectClient(netConnection.Id);
                this.OnClientDisconnected(netConnection as T);
            }

            if (resetBuffer)
            {
                e.UserToken = null;
                e.SetBuffer(null, 0, 0);
                e.Completed -= this.IO_Completed;
                this._writePool.Push(e);
            }
        }

        /// <summary>
        /// Triggered when a <see cref="SocketAsyncEventArgs"/> async operation is completed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (sender == null)
                throw new ArgumentNullException(nameof(sender));

            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    this.ProcessAccept(e);
                    break;
                case SocketAsyncOperation.Receive:
                    this.ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    this.ProcessSend(e);
                    break;
                case SocketAsyncOperation.Disconnect: break;
                default:
                    throw new InvalidOperationException("Unexpected SocketAsyncOperation.");
            }
        }

        #region IDisposable Support

        private bool _disposed;

        /// <summary>
        /// Dispose the <see cref="NetServer{T}"/> resources.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    this.Stop();

                    if (this.Socket != null)
                    {
                        this.Socket.Shutdown(SocketShutdown.Both);
                        this.Socket.Dispose();
                        this.Socket = null;
                    }

                    if (this._readPool != null)
                    {
                        this._readPool.Dispose();
                        this._readPool = null;
                    }

                    if (this._writePool != null)
                    {
                        this._writePool.Dispose();
                        this._writePool = null;
                    }
                }

                _disposed = true;
            }
            else
                throw new ObjectDisposedException(nameof(NetServer<T>));
        }
        
        /// <summary>
        /// Destroys the <see cref="NetServer{T}"/> instance.
        /// </summary>
        ~NetServer()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Dispose the <see cref="NetServer{T}"/> resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
