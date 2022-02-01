using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
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


        [SerializeField] private ushort m_Port = 47776;
        [SerializeField] private long m_UniqueApplicationId;

        private CancellationTokenSource _tokenSource;
        private UdpClient _client;
        private TBroadCast _discoveryData;
        private IPAddress _broadcastIP;
        private IPv6MulticastOption _multicastGroup;

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

        public void ClientBroadcast(TBroadCast request)
        {
            byte[] broadCastMessage = SerializeData(request, MessageType.BroadCast);
            // IPEndPoint broadcastEndPoint = new IPEndPoint(IPAddress.Parse("ff02::1"), m_Port);
            IPEndPoint broadcastEndPoint = new IPEndPoint(_broadcastIP, m_Port);
            _client.Send(broadCastMessage, broadCastMessage.Length, broadcastEndPoint);
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
                _client?.DropMulticastGroup(_multicastGroup.Group, (int) _multicastGroup.InterfaceIndex);
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
            _discoveryData = new TBroadCast();

            _client = new UdpClient(server ? m_Port : 0, AddressFamily.InterNetworkV6);
            // _broadcastIP = GetBroadcastIP();
            _broadcastIP = IPAddress.Parse("ff02::1");
            _multicastGroup = new IPv6MulticastOption(_broadcastIP);

            if (IsClient)
            {
                _client.JoinMulticastGroup(_broadcastIP);
                _ = StartTask(async () => await ReceiveBroadcastResponse(_tokenSource.Token));
            }
            else
            {
                _client.JoinMulticastGroup((int) _multicastGroup.InterfaceIndex, _multicastGroup.Group);
                _ = StartTask(async () => await ReceiveBroadcastRequests(_tokenSource.Token));
            }
        }

        private Task StartTask(Func<Task> callback) =>
            Task.Factory.StartNew(callback,
                _tokenSource.Token,
                TaskCreationOptions.RunContinuationsAsynchronously,
                TaskScheduler.FromCurrentSynchronizationContext());

        protected abstract bool ProcessBroadcast(IPEndPoint sender, TBroadCast broadCast, out TResponse response);
        protected abstract void ResponseReceived(IPEndPoint sender, TResponse response);

        /// <summary>
        /// Client:
        /// </summary>
        /// <param name="token"></param>
        private async Task ReceiveBroadcastResponse(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var serverResponse = await _client.ReceiveAsync();
                var segment = new ArraySegment<byte>(serverResponse.Buffer, 0, serverResponse.Buffer.Length);

                using var reader = new FastBufferReader(segment, Allocator.Temp);

                try
                {
                    if (ReadAndCheckHeader(reader, MessageType.Response) == false)
                        return;

                    reader.ReadNetworkSerializable(out TResponse receivedResponse);
                    ResponseReceived(serverResponse.RemoteEndPoint, receivedResponse);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }


        /// <summary>
        /// Server:
        /// </summary>
        /// <param name="token"></param>
        private async Task ReceiveBroadcastRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var request = await _client.ReceiveAsync();
                var segment = new ArraySegment<byte>(request.Buffer, 0, request.Buffer.Length);
                using var reader = new FastBufferReader(segment, Allocator.Temp);
                await ReplyClient(request.RemoteEndPoint, reader);
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
                    byte[] data = SerializeData(response, MessageType.Response);
                    await _client.SendAsync(data, data.Length, endPoint);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private byte[] SerializeData<TValue>(TValue discoveryData, MessageType messageType) where TValue : INetworkSerializable, new()
        {
            FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp, 1024 * 64);

            byte[] bytes = Array.Empty<byte>();
            try
            {
                WriteHeader(writer, messageType);
                writer.WriteNetworkSerializable(discoveryData);
                bytes = writer.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                writer.Dispose();
            }

            return bytes;
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
                return false;

            reader.ReadByteSafe(out byte messageType);
            if (messageType != (byte) expectedType)
                return false;

            return true;
        }

        public IPAddress GetBroadcastIP()
        {
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                NetworkInterfaceType _type1 = NetworkInterfaceType.Wireless80211;
                NetworkInterfaceType _type2 = NetworkInterfaceType.Ethernet;

                if ((item.NetworkInterfaceType == _type1 || item.NetworkInterfaceType == _type2) &&
                    item.OperationalStatus == OperationalStatus.Up && item.SupportsMulticast)
#endif
                {
                    foreach (var ip in item.GetIPProperties().MulticastAddresses)
                    {
                        Debug.Log($"{item.Name} - {item.Description} = {ip.Address}");
                        if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            return ip.Address;
                        }
                    }
                }
            }

            return IPAddress.IPv6Any;
        }
    }
}