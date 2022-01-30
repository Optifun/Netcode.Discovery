using System;
using System.Net;

namespace Server
{
    [Serializable]
    public class BroadcastData
    {
        public int BroadcastPort { get; set; }
        public int CommunicationPort { get; set; }
        public string Name { get; set; } = Guid.NewGuid().ToString();
    }
}