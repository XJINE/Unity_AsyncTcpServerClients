using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace AsyncTcpServerClients {
public class AsyncTcpServer : MonoBehaviour
{
    #region Field

    [SerializeField] private int  port        = 8080;
    [SerializeField] private int  bufferSize  = 4 * 1024; // 4KB
    [SerializeField] private bool autoStart   = true;
    [SerializeField] private bool logClientId = false;

    public UnityEvent                                 serverStarted;
    public UnityEvent                                 serverStopped;
    public UnityEvent<string, TcpClient>              clientConnected;    // ClientId, Client
    public UnityEvent<string, TcpClient>              clientDisconnected; // ClientId, Client
    public UnityEvent<string, TcpClient, int, byte[]> messageReceived;    // ClientId, Client, dataLength, data
    public UnityEvent<string, TcpClient, byte[]>      messageSent;        // ClientId, Client, data

    public UnityEvent<Exception>                    serverStartFailed;
    public UnityEvent<Exception>                    serverStopFailed;
    public UnityEvent<Exception>                    clientConnectFailed;
    public UnityEvent<string, TcpClient, Exception> clientDisconnectFailed; // ClientId, Client, Exception
    public UnityEvent<string, TcpClient, Exception> messageReceiveFailed;   // ClientId, Client, Exception
    public UnityEvent<string, TcpClient, Exception> messageSendFailed;      // ClientId, Client, Exception

    private TcpListener             _tcpListener;
    private CancellationTokenSource _cancellationTokenSource;

    private readonly ConcurrentDictionary<string, TcpClient> _clients           = new (); // ClientId, Client
    private readonly ConcurrentQueue<Action>                 _mainThreadActions = new ();

    #endregion Field

    #region Property

    public bool IsRunning => _tcpListener != null
                          && _cancellationTokenSource?.Token.IsCancellationRequested == false;

    public ReadOnlyDictionary<string, TcpClient> Clients { get; private set; }

    #endregion Property

    #region Method

    private void Awake()
    {
        Clients = new ReadOnlyDictionary<string, TcpClient>(_clients);
    }

    private void Start()
    {
        if (autoStart)
        {
            StartServer();
        }
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    private void OnDestroy()
    {
        StopServer();
    }

    [ContextMenu(nameof(StartServer))]
    public async void StartServer()
    {
        try
        {
            if (IsRunning)
            {
                throw new Exception($"Server is already running on port {port}");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _tcpListener             = new TcpListener(IPAddress.Any, port);

            _tcpListener.Start();

            Debug.Log($"Server started on port {port}");

            _mainThreadActions.Enqueue(() => { serverStarted.Invoke(); });

            await AcceptClients(_cancellationTokenSource.Token);
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to start server on port {port}\n{exception.Message}");

            _mainThreadActions.Enqueue(() => serverStartFailed.Invoke(exception));
        }
    }

    [ContextMenu(nameof(StopServer))]
    public void StopServer()
    {
        try
        {
            if (!IsRunning)
            {
                throw new Exception($"Server is not running");
            }

            _cancellationTokenSource?.Cancel();
            _tcpListener?.Stop();

            DisconnectClients();

            _clients.Clear();

            Debug.Log("Server stopped");

            _mainThreadActions.Enqueue(() => serverStopped.Invoke());
        }
        catch (Exception exception)
        {
            Debug.LogError($"Error stopping server:\n{exception.Message}");

            _mainThreadActions.Enqueue(() => serverStopFailed.Invoke(exception));
        }
    }

    private async Task AcceptClients(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await AcceptClient(_tcpListener, cancellationToken);

                if (tcpClient != null)
                {
                    var clientId = Guid.NewGuid().ToString();

                    _clients.TryAdd(clientId, tcpClient);

                    Debug.Log($"Client connected: {tcpClient.Client.RemoteEndPoint}{LogClientId(clientId)}");

                    _mainThreadActions.Enqueue(() => clientConnected.Invoke(clientId, tcpClient));

                    _ = ReceiveMessage(clientId, tcpClient, cancellationToken);
                }
            }
            catch (ObjectDisposedException) // Expected exception during server shutdown
            {
                break;
            }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Debug.Log($"Error accepting client: {exception.Message}");

                    _mainThreadActions.Enqueue(() => clientConnectFailed.Invoke(exception));
                }
            }
        }
    }

    private static async Task<TcpClient> AcceptClient(TcpListener tcpListener, CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(tcpListener.Stop))
        {
            try
            {
                return await Task.Run(tcpListener.AcceptTcpClient, cancellationToken);
            }
            catch (ObjectDisposedException) // Expected exception during server shutdown
            {
                return null;
            }
        }
    }

    [ContextMenu(nameof(DisconnectClients))]
    public void DisconnectClients()
    {
        foreach (var clientId in _clients.Keys)
        {
            DisconnectClient(clientId);
        }
    }

    public void DisconnectClient(string clientId)
    {
        DisconnectClient(clientId, null);
    }

    private void DisconnectClient(string clientId, EndPoint endPoint = null)
    {
        if (!_clients.TryRemove(clientId, out var tcpClient))
        {
            // CAUTION:
            // It’s common that the client is already removed because of the shutdown order.
            // But trying to remove it anyway is the right way to close the connection.
            // So it shouldn't be treated as an exception or an error.

            var clientLog = endPoint == null ? $"{clientId}" : $"{endPoint}{LogClientId(clientId)}";

            Debug.Log($"Client not found or already disconnected: {clientLog}");

            return;
        }

        try
        {
            // CAUTION:
            // var remoteEndPoint = tcpClient.Client.RemoteEndPoint;
            // 1) Client.RemoteEndPoint becomes unavailable after client.Close() is called.
            // 2) When the connection is closed from the client side,
            //    "tcpClient.Client.RemoteEndPoint" throws "Cannot access a disposed object.".
            //    In most cases, in such situations, the finally block of ReceiveMessage is called.
            //    So the RemoteEndPoint is available from there.

            var remoteEndPoint = endPoint;

            if (tcpClient.Connected)
            {
                remoteEndPoint = tcpClient.Client.RemoteEndPoint;
            }

            tcpClient.Close();

            Debug.Log($"Client disconnected: {remoteEndPoint}{LogClientId(clientId)}");

            _mainThreadActions.Enqueue(() => clientDisconnected.Invoke(clientId, tcpClient));
        }
        catch (Exception exception)
        {
            var clientLog = _clients.TryGetValue(clientId, out var client) ?
                          $"{client.Client.RemoteEndPoint}{LogClientId(clientId)}" :
                          $"{clientId}";

            Debug.LogError($"Error disconnecting client: {clientLog}\n{exception.Message}");

            _mainThreadActions.Enqueue(() => clientDisconnectFailed.Invoke(clientId, client, exception));
        }
    }

    private async Task ReceiveMessage(string clientId, TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];

        var remoteEndPoint = client.Client.RemoteEndPoint;

        try
        {
            await using var stream = client.GetStream();

            while (client.Connected && !cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0) // Client disconnected
                {
                    break;
                }

                Debug.Log($"Message received from client: {remoteEndPoint}{LogClientId(clientId)}");

                _mainThreadActions.Enqueue(() => messageReceived.Invoke(clientId, client, bytesRead, buffer));
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Debug.LogError($"Message receiving error: {remoteEndPoint}{LogClientId(clientId)}\n{exception.Message}");

                _mainThreadActions.Enqueue(() => messageReceiveFailed.Invoke(clientId, client, exception));
            }
        }
        finally
        {
            DisconnectClient(clientId, remoteEndPoint);
        }
    }

    public async Task<bool> SendMessage(string clientId, byte[] message)
    {
        try
        {
            if (!_clients.TryGetValue(clientId, out var tcpClient))
            {
                throw new Exception($"Client does not exist: {clientId}");
            }

            if (!tcpClient.Connected)
            {
                throw new Exception($"Client not connected: {tcpClient.Client.RemoteEndPoint}{LogClientId(clientId)}");
            }

            await tcpClient.GetStream().WriteAsync(message, 0, message.Length);

            Debug.Log($"Message sent to client: {tcpClient.Client.RemoteEndPoint}{LogClientId(clientId)}");

            _mainThreadActions.Enqueue(()=> messageSent.Invoke(clientId, tcpClient, message));

            return true;
        }
        catch (Exception exception)
        {
            var clientLog = _clients.TryGetValue(clientId, out var client) ?
                          $"{client.Client.RemoteEndPoint}{LogClientId(clientId)}" :
                          $"{clientId}";

            Debug.LogError($"Failed to send message: {clientLog}\n{exception.Message}");

            _mainThreadActions.Enqueue(() => messageSendFailed.Invoke(clientId, client, exception));

            return false;
        }
    }

    public async Task SendMessages(byte[] message)
    {
        var tasks = new List<Task>();

        foreach (var clientId in _clients.Keys)
        {
            tasks.Add(SendMessage(clientId, message));
        }

        await Task.WhenAll(tasks);
    }

    [ContextMenu(nameof(SendDebugMessages))]
    public void SendDebugMessages()
    {
        _ = SendMessages(System.Text.Encoding.UTF8.GetBytes("Debug"));
    }

    private string LogClientId(string clientId)
    {
        return logClientId ? $" ({clientId})" : string.Empty;
    }

    #endregion Method
}}