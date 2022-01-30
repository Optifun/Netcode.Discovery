using System;
using System.Net;

namespace Optifun.Discovery
{
    [Serializable]
    public class BroadcastData
    {
        public int BroadcastPort { get; set; }
        public int CommunicationPort { get; set; }
        public string Name { get; set; } = Guid.NewGuid().ToString();
    }
}