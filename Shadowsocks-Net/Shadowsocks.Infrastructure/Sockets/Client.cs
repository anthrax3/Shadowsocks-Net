﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Logging;
using Argument.Check;

namespace Shadowsocks.Infrastructure.Sockets
{
    public abstract class Client : IClient
    {
        public virtual IPEndPoint EndPoint => _sock.RemoteEndPoint as IPEndPoint;
        public virtual IPEndPoint LocalEndPoint => _sock.LocalEndPoint as IPEndPoint;

        public event EventHandler<ClientEventArgs> Closing;

        protected Socket _sock = null;
        protected ILogger _logger = null;


        public Client(Socket socket, ILogger logger = null)
        {
            this._sock = Throw.IfNull(() => socket);
            this._logger = logger;
        }

        ~Client()
        {
            Close();
        }



        /// <summary>
        /// Receive data from remote.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>-1 if error.</returns>
        /// <exception cref="">no exception</exception>
        public virtual async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (null == _sock) { return -1; }

            int read;
            try
            {
                read = await _sock.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            }
            catch (SocketException se)
            {
                _logger?.LogError(se, "ClientBase ReadAsync error.");
                return -1;
            }
            catch (Exception se)
            {
                _logger?.LogError(se, "ClientBase ReadAsync error.");
                return -1;
            }
            return read;
        }

        /// <summary>
        /// Send data to remote.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>-1 if error.</returns>
        /// <exception cref="">no exception</exception>
        public virtual async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (null == _sock) { return -1; }

            int written;
            try
            {
                written = await _sock.SendAsync(buffer, SocketFlags.None, cancellationToken);
            }
            catch (SocketException se)
            {
                _logger?.LogError(se, "ClientBase WriteAsync error.");
                return -1;
            }
            catch (Exception se)
            {
                _logger?.LogError(se, "ClientBase WriteAsync error.");
                return -1;
            }
            return written;
        }

        /// <summary>
        /// Close it.
        /// </summary>
        /// <exception cref="">no exception</exception>
        public virtual void Close()
        {
            if (null != _sock)
            {
                try
                {
                    _logger?.LogInformation("ClientBase socket closing...");
                    _sock.Shutdown(SocketShutdown.Both);
                    _sock.Close();
                    _logger?.LogInformation("ClientBase socket closed.");
                }
                catch (SocketException ex)//(SocketException se)                
                {
                    _logger?.LogError(ex, "ClientBase close socket error.");
                }
                finally { _sock = null; }

                FireClosing();


            }
        }

        protected virtual void FireClosing()
        {
            try
            {
                if (null != Closing)
                {
                    Closing(this, new ClientEventArgs(this));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ClientBase error fire Closing.");
            }
        }
    }
}
