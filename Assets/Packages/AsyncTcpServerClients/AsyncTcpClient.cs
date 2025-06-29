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

    [SerializeField] private string serverHost  = "127.0.0.1";
    [SerializeField] private int    serverPort  = 8080;
    [SerializeField] private int    bufferSize  = 4 * 1024; // 4 KB
    [SerializeField] private bool   autoConnect = false;

    public UnityEvent              connected;
    public UnityEvent              disconnected;
    public UnityEvent<int, byte[]> messageReceived;
    public UnityEvent<byte[]>      messageSent;

    public UnityEvent<Exception> connectFailed;
    public UnityEvent<Exception> messageReceiveFailed;
    public UnityEvent<Exception> messageSendFailed;

    private TcpClient               _tcpClient;
    private NetworkStream           _stream;
    private CancellationTokenSource _cancellationTokenSource;

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    #endregion Field

    #region Property

    public bool IsConnected => _tcpClient?.Connected ?? false;

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

    private void OnDestroy()
    {
        Disconnect();
    }

    public async void Connect()
    {
        try
        {
            if (IsConnected)
            {
                throw new Exception("Already connected to server");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _tcpClient               = new TcpClient();

            await _tcpClient.ConnectAsync(serverHost, serverPort);

            _stream = _tcpClient.GetStream();

            Debug.Log($"Connected to server {serverHost}:{serverPort}");

            _mainThreadActions.Enqueue(() => connected.Invoke());

            _ = ReceiveMessage(_cancellationTokenSource.Token);
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to connect: {exception.Message}");

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
        try
        {
            if (!IsConnected)
            {
                throw new Exception("Not connected to server");
            }

            _cancellationTokenSource?.Cancel();
            _stream?                 .Close();
            _tcpClient?              .Close();
        }
        catch (Exception exception)
        {
            Debug.LogError($"Error during disconnect: {exception.Message}");
        }
        finally
        {
            _stream    = null;
            _tcpClient = null;

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

                Debug.Log("Message received from server");

                _mainThreadActions.Enqueue(() => messageReceived.Invoke(bytesRead, buffer));
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Debug.LogError($"Failed to receive message: {exception.Message}");

                _mainThreadActions.Enqueue(() => messageReceiveFailed.Invoke(exception));
            }
        }
        finally
        {
            if (IsConnected)
            {
                Disconnect();
            }
        }
    }

    public async Task<bool> SendMessage(byte[] message)
    {
        try
        {
            if (!IsConnected)
            {
                throw new Exception("Not connected to server");
            }

            await _stream.WriteAsync(message, 0, message.Length);

            Debug.Log("Message sent to server");

            _mainThreadActions.Enqueue(() => messageSent.Invoke(message));

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to send message: {exception.Message}");

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