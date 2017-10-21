﻿using System;

namespace Ether.Network.Core
{
    public interface INetPacketStream : IDisposable
    {
        int Size { get; }

        long Position { get; }

        byte[] Buffer { get; }

        T Read<T>();

        T[] Read<T>(int amount);

        void Write<T>(T value);
    }
}
