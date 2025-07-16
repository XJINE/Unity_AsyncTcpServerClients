using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace AsyncTcpServerClients {
public class AsyncTcpClient : MonoBehaviour
{
    #region Field

    [SerializeField] private string serverHost    = "127.0.0.1";
    [SerializeField] private int    serverPort    = 8080;
    [SerializeField] private int    bufferSize    = 4 * 1024; // 4 KB
    [SerializeField] private bool   autoConnect   = false;
    [SerializeField] private bool   autoReconnect = false;

    public UnityEvent              connected;
    public UnityEvent              disconnected;
    public UnityEvent<int, byte[]> messageReceived;
    public UnityEvent<byte[]>      messageSent;

    public UnityEvent<Exception> connectFailed;
    public UnityEvent<Exception> messageReceiveFailed;
    public UnityEvent<Exception> messageSendFailed;

    private NetworkStream           _stream;
    private CancellationTokenSource _cancellationTokenSource;

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    #endregion Field

    #region Property

    public TcpClient TcpClient { get; private set; }

    public bool IsConnected => TcpClient?.Connected ?? false;

    private bool Disabling { get; set; }

    #endregion

    #region Method

    private void Start()
    {
        if (autoConnect)
        {
            Connect();
        }
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    private void OnEnable()
    {
        Disabling = false;
    }

    private void OnDisable()
    {
        Disabling = true;
        Disconnect();
    }

    [ContextMenu(nameof(Connect))]
    public async void Connect()
    {
        try
        {
            if (IsConnected)
            {
                throw new Exception($"Already connected to server {serverHost}:{serverPort}");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            TcpClient                = new TcpClient();

            await TcpClient.ConnectAsync(serverHost, serverPort);

            _stream = TcpClient.GetStream();

            Debug.Log($"Connected to server {serverHost}:{serverPort}");

            _mainThreadActions.Enqueue(() => connected.Invoke());

            _ = ReceiveMessage(_cancellationTokenSource.Token); // _ = means discard.
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to connect: {serverHost}:{serverPort}\n{exception.Message}");

            _mainThreadActions.Enqueue(() => connectFailed.Invoke(exception));

            if (!IsConnected)
            {
                Disconnect();
            }
        }
    }

    [ContextMenu(nameof(Disconnect))]
    public void Disconnect()
    {
        if (!IsConnected)
        {
            if (Disabling)
            {
                return;
            }

            // CAUTION:
            // Disconnect() is called when this instance is destroyed,
            // even if it’s not connected to the server.
            // Therefore, "IsConnected" returning false is the expected behavior.

            Debug.Log($"Not connected to server {serverHost}:{serverPort}");
            return;
        }
        try
        {
            _cancellationTokenSource?.Cancel();
            _stream?                 .Close();
            TcpClient?               .Close();
        }
        catch (Exception exception)
        {
            Debug.LogError($"Error during disconnect: {serverHost}:{serverPort}\n{exception.Message}");
        }
        finally
        {
            _stream   = null;
            TcpClient = null;

            Debug.Log($"Disconnected from server {serverHost}:{serverPort}");

            _mainThreadActions.Enqueue(() => disconnected.Invoke());
        }
    }

    private async Task ReceiveMessage(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[bufferSize];

            while (IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0)
                {
                    break; // Server has closed the connection
                }

                Debug.Log($"Message received from server {serverHost}:{serverPort}");

                _mainThreadActions.Enqueue(() => messageReceived.Invoke(bytesRead, buffer));
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Debug.LogError($"Failed to receive message: {serverHost}:{serverPort}\n{exception.Message}");

                _mainThreadActions.Enqueue(() => messageReceiveFailed.Invoke(exception));
            }
        }
        finally
        {
            if (IsConnected)
            {
                Disconnect();
            }

            if (autoReconnect && !Disabling)
            {
                Connect();
            }
        }
    }

    public async Task<bool> SendMessage(byte[] message)
    {
        try
        {
            if (!IsConnected)
            {
                throw new Exception($"Not connected to server {serverHost}:{serverPort}");
            }

            await _stream.WriteAsync(message, 0, message.Length);

            Debug.Log($"Message sent to server {serverHost}:{serverPort}");

            _mainThreadActions.Enqueue(() => messageSent.Invoke(message));

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to send message: {serverHost}:{serverPort}\n{exception.Message}");

            _mainThreadActions.Enqueue(() => messageSendFailed.Invoke(exception));

            return false;
        }
    }

    [ContextMenu(nameof(SendDebugMessage))]
    public void SendDebugMessage()
    {
        _ = SendMessage(System.Text.Encoding.UTF8.GetBytes("Debug"));
    }

    #endregion Method
}}