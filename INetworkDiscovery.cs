using Unity.Netcode;

public interface INetworkDiscovery<TRequest> where TRequest: INetworkSerializable, new()
{
    /// <summary>
    /// Gets a value indicating whether the discovery is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets whether the discovery is in server mode.
    /// </summary>
    bool IsServer { get; }

    /// <summary>
    /// Gets whether the discovery is in client mode.
    /// </summary>
    bool IsClient { get; }

    void SetRequestContent(TRequest request);

    /// <summary>
    /// Starts the discovery in server mode which will respond to client broadcasts searching for servers.
    /// </summary>
    void StartServer();

    /// <summary>
    /// Starts the discovery in client mode. <see cref="NetworkDiscovery{TBroadCast,TResponse}.ClientBroadcast"/> can be called to send out broadcasts to servers and the client will actively listen for responses.
    /// </summary>
    void StartClient();

    void StopDiscovery();
}