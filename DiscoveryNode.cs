using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Threading;

namespace Optifun.Discovery
{
    internal class DiscoveryNode
    {
        public event Action<IPEndPoint> ClientFound;

        public int BroadcastInterval { get; set; } = 300;

        private readonly UdpClient _client;
        private readonly SynchronizationContext _sync;
        private readonly string _name;
        private readonly int _sendBroadcastPort;
        private readonly int _receiveBroadcastPort;
        private readonly bool _blockLocalhostDiscovery;
        private readonly IPAddress _networkAddress;
        private CancellationTokenSource _token;


        public DiscoveryNode(string name, int receiveBroadcastPort, int sendBroadcastPort)
        {
            _receiveBroadcastPort = receiveBroadcastPort;
            _name = name;
            _sendBroadcastPort = sendBroadcastPort;
            _blockLocalhostDiscovery = receiveBroadcastPort == sendBroadcastPort;
            _client = new UdpClient(receiveBroadcastPort) { EnableBroadcast = true };
            // _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _sync = SynchronizationContext.Current ?? new SynchronizationContext();
            _networkAddress = GetNetworkAddress();
        }

        public DiscoveryNode(string name, int receiveBroadcastPort)
            : this(name, receiveBroadcastPort, receiveBroadcastPort)
        {
        }

        /// <summary>
        /// Starts discovery in background thread
        /// </summary>
        /// <param name="revealSelf">Block self discovery</param>
        /// <param name="discover">Block discovery of network clients</param>
        public void StartDiscovery(bool revealSelf = true, bool discover = true)
        {
            if (!_token?.IsCancellationRequested ?? false)
                StopDiscovery();

            if (!revealSelf && !discover)
                throw new ArgumentException("Two-way discovery blocked", nameof(discover));

            _token = new CancellationTokenSource();
            if (revealSelf)
                Task.Factory.StartNew(() => BroadCast(_token.Token), _token.Token);

            if (discover)
                Task.Factory.StartNew(() => ReceiveBroadcast(_token.Token), _token.Token);
        }

        public void StopDiscovery() =>
            _token.Cancel();

        private void BroadCast(CancellationToken token)
        {
            byte[] broadCastMessage = Encoding.UTF8.GetBytes(_name);
            while (!token.IsCancellationRequested)
            {
                _client.Send(broadCastMessage, broadCastMessage.Length, new IPEndPoint(IPAddress.Broadcast, _sendBroadcastPort));
                Thread.Sleep(BroadcastInterval);
            }
        }

        private void ReceiveBroadcast(CancellationToken token)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                byte[] serverResponse = _client.Receive(ref endPoint);
                if (_blockLocalhostDiscovery && (Equals(endPoint.Address, IPAddress.Loopback) || Equals(endPoint.Address, _networkAddress)))
                    continue;

                string response = Encoding.UTF8.GetString(serverResponse);
                OnClientFound(endPoint);
            }
        }

        private void OnClientFound(IPEndPoint ip) =>
            //"?" нужен, чтобы при отсутствии подписчиков на данное событие ничего не происходило
            _sync.Post((_) => { ClientFound?.Invoke(ip); }, null);

        private static IPAddress GetNetworkAddress() =>
            Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
    }
}
