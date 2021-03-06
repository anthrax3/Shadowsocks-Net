﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Argument.Check;


namespace Shadowsocks.Local
{
    using Infrastructure;
    using Infrastructure.Sockets;
    using Infrastructure.Pipe;
    using Cipher;
    sealed class StandardLocalSocks5Handler : ISocks5Handler
    {

        ILogger _logger = null;

        List<DefaultPipe> _pipes = null;
        object _pipesReadWriteLock = new object();

        Type _cipherType = null;
        string _cipherPassword = null;
        IPEndPoint _remoteServerEndPoint = null;



        //TODO remote server loader
        public StandardLocalSocks5Handler(Type cipherType, string cipherPassword, IPEndPoint remoteServerEndPoint, ILogger logger = null)
        {
            _pipes = new List<DefaultPipe>();
            _cipherType = Throw.IfNull(() => cipherType);
            _cipherPassword = Throw.IfNullOrEmpty(() => cipherPassword);
            _remoteServerEndPoint = Throw.IfNull(() => remoteServerEndPoint);
            _logger = logger;
        }
        ~StandardLocalSocks5Handler()
        {
            Cleanup();
        }


        public async Task HandleTcp(IClient client, CancellationToken cancellationToken)
        {
            if (null == client) { return; }
            if (!await Handshake(client, cancellationToken)) { return; } //Handshake

            using (SmartBuffer request = SmartBuffer.Rent(300), response = SmartBuffer.Rent(300))
            {
                request.SignificantLength = await client.ReadAsync(request.Memory, cancellationToken);
                if (5 >= request.SignificantLength) { client.Close(); return; }

                #region socks5
                /*
                +----+-----+-------+------+----------+----------+
                |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
                +----+-----+-------+------+----------+----------+
                | 1  |  1  | X'00' |  1   | Variable |    2     |
                +----+-----+-------+------+----------+----------+

                 Where:

                      o  VER    protocol version: X'05'
                      o  CMD
                         o  CONNECT X'01'
                         o  BIND X'02'
                         o  UDP ASSOCIATE X'03'
                      o  RSV    RESERVED
                      o  ATYP   address type of following address
                         o  IP V4 address: X'01'
                         o  DOMAINNAME: X'03'
                         o  IP V6 address: X'04'
                      o  DST.ADDR       desired destination address
                      o  DST.PORT desired destination port in network octet
                         order


                +----+-----+-------+------+----------+----------+
                |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
                +----+-----+-------+------+----------+----------+
                | 1  |  1  | X'00' |  1   | Variable |    2     |
                +----+-----+-------+------+----------+----------+

                 Where:

                      o  VER    protocol version: X'05'
                      o  REP    Reply field:
                         o  X'00' succeeded
                         o  X'01' general SOCKS server failure
                         o  X'02' connection not allowed by ruleset
                         o  X'03' Network unreachable
                         o  X'04' Host unreachable
                         o  X'05' Connection refused
                         o  X'06' TTL expired
                         o  X'07' Command not supported
                         o  X'08' Address type not supported
                         o  X'09' to X'FF' unassigned
                      o  RSV    RESERVED
                      o  ATYP   address type of following address
                         o  IP V4 address: X'01'
                         o  DOMAINNAME: X'03'
                         o  IP V6 address: X'04'
                      o  BND.ADDR       server bound address
                      o  BND.PORT       server bound port in network octet order
                 */

                #endregion                

                switch (request.Memory.Span[1])
                {
                    case 0x1://connect //TODO validate addr                             
                        {
                            var relayClient = await TcpClient1.ConnectAsync(_remoteServerEndPoint, _logger);
                            if (null == relayClient)//unable to connect ss-remote
                            {
                                _logger?.LogInformation($"unable to connect ss-remote:[{_remoteServerEndPoint.ToString()}]");
                                await client.WriteAsync(NegotiationResponse.CommandConnectFailed, cancellationToken);
                                client.Close();
                                break;
                            }
                            await client.WriteAsync(NegotiationResponse.CommandConnectOK, cancellationToken);//ready to relay.

                            using (SmartBuffer relayRequest = SmartBuffer.Rent(Defaults.ReceiveBufferSize))
                            {
                                var relayStream = new MemoryWriter(relayRequest.Memory);
                                var clientRequest = request;
                                byte atyp = clientRequest.Memory.Span[3];
                                int sliceLen = 0x1 == atyp ? 1 + 4 + 2 : 1 + 16 + 2;
                                sliceLen = 0x3 == atyp ? 1 + 1 + clientRequest.Memory.Span[4] + 2 : sliceLen;
                                relayStream.Write(clientRequest.Memory.Slice(3, sliceLen));//addr
                                relayRequest.SignificantLength = relayStream.Position;

                                relayRequest.SignificantLength += //payload
                                    await client.ReadAsync(relayRequest.Memory.Slice(relayRequest.SignificantLength), cancellationToken);

                                await PipeTcp(client, relayClient, relayRequest, cancellationToken);//pipe

                            }
                        }
                        break;
                    case 0x2://bind
                        {
                            request.Memory.Slice(0, request.SignificantLength).CopyTo(response.Memory);
                            response.Memory.Span[1] = 0x7;
                            await client.WriteAsync(response.Memory.Slice(0, request.SignificantLength), cancellationToken);
                            client.Close();
                        }
                        break;
                    case 0x3://udp assoc
                        {
                            var responseWriter = new MemoryWriter(response.Memory);

                            byte adtp = (byte)(AddressFamily.InterNetworkV6 == client.LocalEndPoint.AddressFamily ? 0x4 : 0x1);
                            var addrBytes = client.LocalEndPoint.Address.GetAddressBytes();
                            var portBytes = BitConverter.GetBytes((ushort)IPAddress.HostToNetworkOrder((short)client.LocalEndPoint.Port));
                            byte addrlen = (byte)addrBytes.Length;

                            responseWriter.WriteByte(0x5, 0x0, 0x0, 0x0, adtp, addrlen);
                            responseWriter.Write(addrBytes.AsSpan());
                            responseWriter.Write(portBytes.AsSpan());
                            response.SignificantLength = responseWriter.Position;
                            await client.WriteAsync(response.Memory.Slice(0, response.SignificantLength), cancellationToken);

                            //TODO
                            client.Closing += this.Client_Closing;
                        }
                        break;
                    default:
                        break;
                }

            }

        }


        public async Task HandleUdp(IClient client, CancellationToken cancellationToken = default)
        {
            if (null == client) { return; }

            //authentication //TODO udp assoc

            var relayClient = await UdpClient1.ConnectAsync(this._remoteServerEndPoint, _logger);
            if (null == relayClient)
            {
                _logger?.LogInformation($"unable to relay udp");
                client.Close();
            }
            await PipeUdp(client, relayClient, cancellationToken);

        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="client"></param>
        /// <param name="relayClient"></param>
        /// <param name="initStream">[target address][payload]</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task PipeTcp(IClient client, IClient relayClient, SmartBuffer initStream, CancellationToken cancellationToken)
        {
            var cipher = Activator.CreateInstance(this._cipherType, this._cipherPassword) as IShadowsocksStreamCipher;
            using (var relayRequestCipher = cipher.EncryptTcp(initStream.Memory.Slice(0, initStream.SignificantLength)))
            {
                int written = await relayClient.WriteAsync(relayRequestCipher.Memory.Slice(0, relayRequestCipher.SignificantLength), cancellationToken);
                _logger?.LogInformation($"PipeTcp written={written}, relayRequestCipher={relayRequestCipher.SignificantLength}.");


                DefaultPipe pipe = new DefaultPipe(client, relayClient, Defaults.ReceiveBufferSize, _logger);
                PipeFilter filter = new Cipher.CipherTcpFilter(relayClient, cipher, _logger);
                pipe.ApplyFilter(filter);
                pipe.OnBroken += this.Pipe_OnBroken;
                lock (_pipesReadWriteLock)
                {
                    this._pipes.Add(pipe);
                }
                pipe.Pipe();

            }
        }


        async Task PipeUdp(IClient client, IClient relayClient, CancellationToken cancellationToken)
        {
            //authentication //TODO udp assoc
            var cipher = Activator.CreateInstance(this._cipherType, this._cipherPassword) as IShadowsocksStreamCipher;
            DefaultPipe pipe = new DefaultPipe(client, relayClient, Defaults.ReceiveBufferSize, _logger);

            PipeFilter filter = new Cipher.CipherUdpFilter(relayClient, cipher, _logger);
            PipeFilter filter2 = new LocalUdpRelayPackingFilter(relayClient, _logger);

            pipe.ApplyFilter(filter)
                .ApplyFilter(filter2);

            pipe.OnBroken += this.Pipe_OnBroken;

            lock (_pipesReadWriteLock)
            {
                this._pipes.Add(pipe);
            }
            pipe.Pipe();

            await Task.CompletedTask;
        }

        void Cleanup()
        {
            foreach (var p in this._pipes)
            {
                p.UnPipe();
                p.ClientA.Close();
                p.ClientB.Close();
            }
            lock (_pipesReadWriteLock)
            {
                this._pipes.Clear();
            }

        }
        public void Dispose()
        {
            Cleanup();
        }

        private void Client_Closing(object sender, ClientEventArgs e)
        {
            e.Client.Closing -= this.Client_Closing;

            //tcp lost->remove udp
            //TODO disassoc udp
        }

        private void Pipe_OnBroken(object sender, PipeEventArgs e)
        {
            var p = e.Pipe as DefaultPipe;
            p.OnBroken -= this.Pipe_OnBroken;
            p.UnPipe();
            p.ClientA.Close();
            p.ClientB.Close();

            lock (_pipesReadWriteLock)
            {
                this._pipes.Remove(p);
            }
        }







        async Task<bool> Handshake(IClient client, CancellationToken cancellationToken)
        {
            using (var request = SmartBuffer.Rent(300))
            {
                if (2 < await client.ReadAsync(request.Memory, cancellationToken))
                {
                    if (request.Memory.Span[0] != 0x5)//accept socks5 only.
                    {
                        await client.WriteAsync(NegotiationResponse.HandshakeReject, cancellationToken);
                        client.Close();
                        return false;
                    }
                    else
                    {
                        return 2 == await client.WriteAsync(NegotiationResponse.HandshakeAccept, cancellationToken);
                    }
                }
                client.Close();
                return false;
            }

        }
        class NegotiationResponse
        {
            public static readonly byte[] HandshakeAccept = { 0x5, 0x0 };
            public static readonly byte[] HandshakeReject = { 0x5, 0xFF };
            public static readonly byte[] CommandConnectOK = { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            public static readonly byte[] CommandConnectFailed = { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 };
        }
    }
}
