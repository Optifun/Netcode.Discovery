using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Optifun.Discovery
{
    public class BroadcastClient
    {
        public event Action<IPEndPoint, BroadcastData> ServerFound;

        public BroadcastData DiscoveryData { get; private set; }
        public int BroadcastInterval { get; set; } = 300;
        public int BroadcastPort { get; }

        private readonly UdpClient _client;
        private readonly SynchronizationContext _sync;
        private readonly int _sendBroadcastPort;
        private readonly IPAddress _networkAddress;
        private readonly BinaryFormatter _binaryFormatter;
        private CancellationTokenSource _token;

        public BroadcastClient(int sendBroadcastPort)
        {
            _sendBroadcastPort = sendBroadcastPort;
            _binaryFormatter = new BinaryFormatter();
            // 0 - занимает любой свободный порт
            _client = new UdpClient(0) {EnableBroadcast = true};
            _sync = SynchronizationContext.Current ?? new SynchronizationContext();
            BroadcastPort = ((IPEndPoint) _client.Client.LocalEndPoint).Port;
            _networkAddress = GetNetworkAddress();
        }

        /// <summary>
        /// Starts discovery in background thread
        /// </summary>
        /// <param name="data"></param>
        public void StartDiscovery(BroadcastData data)
        {
            DiscoveryData = data;
            if (!_token?.IsCancellationRequested ?? false)
                StopDiscovery();

            _token = new CancellationTokenSource();
            Task.Factory.StartNew(() => BroadCast(_token.Token), _token.Token);
            Task.Factory.StartNew(() => ReceiveBroadcast(_token.Token), _token.Token);
        }

        public void StopDiscovery() =>
            _token.Cancel();

        private void BroadCast(CancellationToken token)
        {
            byte[] broadCastMessage = SerializeData(DiscoveryData);
            IPEndPoint broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, _sendBroadcastPort);
            while (!token.IsCancellationRequested)
            {
                _client.Send(broadCastMessage, broadCastMessage.Length, broadcastEndPoint);
                Thread.Sleep(BroadcastInterval);
            }
        }

        private void ReceiveBroadcast(CancellationToken token)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                byte[] serverResponse = _client.Receive(ref endPoint);
                BroadcastData data = DeserializeData(serverResponse);
                if (data == null) continue;
                OnServerFound(endPoint, data);
            }
        }

        private void OnServerFound(IPEndPoint ip, BroadcastData data) =>
            //"?" нужен, чтобы при отсутствии подписчиков на данное событие ничего не происходило
            _sync.Post((_) => { ServerFound?.Invoke(ip, data); }, null);

        private static IPAddress GetNetworkAddress() =>
            Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .First(ip => ip.AddressFamily == AddressFamily.InterNetwork);

        private byte[] SerializeData(BroadcastData data)
        {
            using (var stream = new MemoryStream())
            {
                _binaryFormatter.Serialize(stream, data);
                return stream.GetBuffer();
            }
        }

        private BroadcastData DeserializeData(byte[] buffer)
        {
            using (var stream = new MemoryStream(buffer))
            {
                return _binaryFormatter.Deserialize(stream) as BroadcastData;
            }
        }
    }
}