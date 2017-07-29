﻿using Ether.Network.Exceptions;
using Ether.Network.Packets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ether.Network
{
    public abstract class NetServer<T> : INetServer, IDisposable where T : NetConnection, new()
    {
        private readonly ConcurrentBag<T> _clients;
        private readonly ManualResetEvent _resetEvent;
        private readonly SocketAsyncEventArgs _acceptArgs;

        private bool _isRunning;
        private BufferManager _bufferManager;
        private SocketAsyncEventArgsPool _readPool;
        private SocketAsyncEventArgsPool _writePool;

        protected Socket Socket { get; private set; }

        protected NetServerConfiguration Configuration { get; private set; }

        public bool IsRunning => this._isRunning;

        protected NetServer()
        {
            this.Configuration = new NetServerConfiguration(this);
            this.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this._clients = new ConcurrentBag<T>();
            this._resetEvent = new ManualResetEvent(false);
            this._isRunning = false;
            this._acceptArgs = new SocketAsyncEventArgs();
            this._acceptArgs.Completed += this.IO_Completed;
        }

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

        public void Stop()
        {
            if (this._isRunning)
            {
                this._isRunning = false;
                this._resetEvent.Set();
            }
        }

        protected abstract void Initialize();

        protected abstract void OnClientConnected(T connection);

        protected abstract void OnClientDisconnected(T connection);

        protected virtual IReadOnlyCollection<NetPacketBase> SplitPackets(byte[] buffer)
        {
            return NetPacket.Split(buffer);
        }

        private void StartAccept()
        {
            if (this._acceptArgs.AcceptSocket != null)
                this._acceptArgs.AcceptSocket = null;
            if (!this.Socket.AcceptAsync(this._acceptArgs))
                this.ProcessAccept(this._acceptArgs);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var client = new T();
                client.Initialize(e.AcceptSocket, this.SendData);

                this._clients.Add(client);
                this.OnClientConnected(client);
                
                SocketAsyncEventArgs readArgs = this._readPool.Pop();
                readArgs.UserToken = client;
                readArgs.Completed += this.IO_Completed;
                this._bufferManager.SetBuffer(readArgs);

                if (!e.AcceptSocket.ReceiveAsync(readArgs))
                    this.ProcessReceive(readArgs);
            }

            e.AcceptSocket = null;
            this.StartAccept();
        }

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
                this.OnClientDisconnected(e.UserToken as T);
                e.UserToken = null;
                e.Completed -= this.IO_Completed;
                this._bufferManager.FreeBuffer(e);
                this._readPool.Push(e);
            }
        }

        private void DispatchPackets(NetConnection netConnection, SocketAsyncEventArgs e)
        {
            var buffer = new byte[e.BytesTransferred];

            Buffer.BlockCopy(e.Buffer, e.Offset, buffer, 0, e.BytesTransferred);
            IReadOnlyCollection<NetPacketBase> packets = this.SplitPackets(buffer);

            foreach (var packet in packets)
                netConnection.HandleMessage(packet);
        }

        private void SendData(NetConnection sender, byte[] buffer)
        {
            SocketAsyncEventArgs sendArg = this._writePool.Pop();
            sendArg.UserToken = sender;
            sendArg.Completed += this.IO_Completed;
            sendArg.SetBuffer(buffer, 0, buffer.Length);

            if (sender.Socket != null && !sender.Socket.SendAsync(sendArg))
                this.ProcessSend(sendArg);
        }

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            var netConnection = e.UserToken as NetConnection;
            bool cleanup = true;

            if (e.SocketError == SocketError.Success)
            {
                if (e.BytesTransferred < e.Buffer.Length)
                {
                    cleanup = false;
                    e.SetBuffer(e.BytesTransferred, e.Buffer.Length - e.BytesTransferred);
                    if (netConnection.Socket != null && !netConnection.Socket.SendAsync(e))
                        this.ProcessSend(e);
                }
            }
            else
            {
                Console.WriteLine("Disconnected ProcessSend()");
            }

            if (cleanup)
            {
                e.UserToken = null;
                e.SetBuffer(null, 0, 0);
                e.Completed -= this.IO_Completed;
                this._writePool.Push(e);
            }
        }

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
            }
        }

        #region IDisposable Support

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    this.Stop();

                    if (this.Socket != null)
                    {
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
        
        ~NetServer()
        {
            this.Dispose(false);
        }
        
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
