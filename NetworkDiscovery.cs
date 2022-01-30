using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Optifun.Discovery
{
    public abstract class NetworkDiscovery<TBroadCast, TResponse> : MonoBehaviour, INetworkDiscovery<TBroadCast> where TBroadCast : INetworkSerializable, new()
        where TResponse : INetworkSerializable, new()
    {
        private enum MessageType : byte
        {
            BroadCast = 0,
            Response = 1,
        }

        public bool IsRunning { get; private set; }
        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }

        public int BroadcastInterval
        {
            get => _broadcastInterval;
            set => _broadcastInterval = value;
        }

        [SerializeField] private ushort m_Port = 47776;
        [SerializeField] private long m_UniqueApplicationId;
        [SerializeField] private int _broadcastInterval;

        private SynchronizationContext _sync;
        private CancellationTokenSource _tokenSource;
        private UdpClient _client;
        private TBroadCast _discoveryData;

        private void OnApplicationQuit()
        {
            StopDiscovery();
        }

        private void OnValidate()
        {
            if (m_UniqueApplicationId == 0)
            {
                var value1 = (long) Random.Range(int.MinValue, int.MaxValue);
                var value2 = (long) Random.Range(int.MinValue, int.MaxValue);
                m_UniqueApplicationId = value1 + (value2 << 32);
            }
        }

        // public void ClientBroadcast(TBroadCast request)
        // {
        //     if (!IsClient)
        //     {
        //         throw new InvalidOperationException("Cannot send client broadcast while not running in client mode. Call StartClient first.");
        //     }
        //
        //
        //     _discoveryData = request;
        //     
        //     IPEndPoint endPoint = new IPEndPoint(IPAddress.Broadcast, m_Port);
        //
        //     var data = SerializeData(_discoveryData);
        //
        //     try
        //     {
        //         _client.SendAsync(data, data.Length, endPoint);
        //     }
        //     catch (Exception e)
        //     {
        //         Debug.LogError(e);
        //     }
        // }

        public void SetRequestContent(TBroadCast request)
        {
            _discoveryData = request;
        }

        public void StartServer() =>
            StartDiscovery(true);

        public void StartClient() =>
            StartDiscovery(false);

        public void StopDiscovery()
        {
            IsClient = false;
            IsServer = false;
            IsRunning = false;

            try
            {
                _tokenSource?.Cancel();
                _client?.Close();
            }
            catch (Exception e)
            {
            }

            _client = null;
        }

        private void StartDiscovery(bool server)
        {
            StopDiscovery();
            IsServer = server;
            IsClient = !server;

            _tokenSource = new CancellationTokenSource();
            _sync = SynchronizationContext.Current;

            _client = new UdpClient(server ? m_Port : 0) {EnableBroadcast = true, MulticastLoopback = false};
            if (IsClient)
            {
                Task.Factory.StartNew(() => SendBroadcast(_tokenSource.Token), _tokenSource.Token);
                Task.Factory.StartNew(() => ReceiveBroadcastResponse(_tokenSource.Token), _tokenSource.Token);
            }
            else
            {
                Task.Factory.StartNew(() => ReceiveBroadcastRequests(_tokenSource.Token), _tokenSource.Token);
            }
        }

        protected abstract bool ProcessBroadcast(IPEndPoint sender, TBroadCast broadCast, out TResponse response);
        protected abstract void ResponseReceived(IPEndPoint sender, TResponse response);


        /// <summary>
        /// Client:
        /// </summary>
        /// <param name="token"></param>
        private void SendBroadcast(CancellationToken token)
        {
            byte[] broadCastMessage = SerializeData(_discoveryData);
            IPEndPoint broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, m_Port);
            while (!token.IsCancellationRequested)
            {
                _client.Send(broadCastMessage, broadCastMessage.Length, broadcastEndPoint);
                Thread.Sleep(BroadcastInterval);
            }
        }

        /// <summary>
        /// Client:
        /// </summary>
        /// <param name="token"></param>
        private void ReceiveBroadcastResponse(CancellationToken token)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                byte[] serverResponse = _client.Receive(ref endPoint);
                var segment = new ArraySegment<byte>(serverResponse, 0, serverResponse.Length);

                using (var reader = new FastBufferReader(segment, Allocator.Temp))
                {
                    if (ValidateResponse(reader, out TResponse data))
                        ResponseReceived(endPoint, data);
                }
            }
        }


        /// <summary>
        /// Server:
        /// </summary>
        /// <param name="token"></param>
        private async Task ReceiveBroadcastRequests(CancellationToken token)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                byte[] requestData = _client.Receive(ref endPoint);
                var segment = new ArraySegment<byte>(requestData, 0, requestData.Length);
                using (var reader = new FastBufferReader(segment, Allocator.Temp))
                {
                    await ReplyClient(endPoint, reader);
                }
            }
        }

        /// <summary>
        /// Server: read and reply to client's broadcast request
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="reader"></param>
        private async Task ReplyClient(IPEndPoint endPoint, FastBufferReader reader)
        {
            try
            {
                if (ReadAndCheckHeader(reader, MessageType.BroadCast) == false)
                    return;

                reader.ReadNetworkSerializable(out TBroadCast receivedBroadcast);

                if (ProcessBroadcast(endPoint, receivedBroadcast, out TResponse response))
                {
                    byte[] data = SerializeData(response);
                    await _client.SendAsync(data, data.Length, endPoint);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }


        /// <summary>
        /// Client: read and validate server response 
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        private bool ValidateResponse(FastBufferReader reader, out TResponse response)
        {
            try
            {
                if (ReadAndCheckHeader(reader, MessageType.Response) == false)
                {
                    response = default;
                    return false;
                }

                reader.ReadNetworkSerializable(out TResponse receivedResponse);
                response = receivedResponse;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            response = default;
            return false;
        }

        private byte[] SerializeData<TValue>(TValue discoveryData) where TValue : INetworkSerializable, new()
        {
            using (FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp, 1024 * 64))
            {
                WriteHeader(writer, MessageType.BroadCast);

                writer.WriteNetworkSerializable(discoveryData);
                return writer.ToArray();
            }
        }


        private void WriteHeader(FastBufferWriter writer, MessageType messageType)
        {
            writer.WriteValueSafe(m_UniqueApplicationId);

            writer.WriteByteSafe((byte) messageType);
        }

        private bool ReadAndCheckHeader(FastBufferReader reader, MessageType expectedType)
        {
            reader.ReadValueSafe(out long receivedApplicationId);
            if (receivedApplicationId != m_UniqueApplicationId)
            {
                return false;
            }

            reader.ReadByteSafe(out byte messageType);
            if (messageType != (byte) expectedType)
            {
                return false;
            }

            return true;
        }
    }
}