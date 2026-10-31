namespace ErpNet.FP.Core.Transports
{
    using System;
    using System.Collections.Generic;
    using System.Net.Sockets;
    using Serilog;

    /// <summary>
    /// TCP/IP transport.
    /// </summary>
    /// <seealso cref="ErpNet.FP.Core.Transport" />
    public class TcpTransport : Transport
    {
        public override string TransportName => "tcp";

        protected readonly int DefaultPort = 9100;

        // After a failed connect the address is not retried for this long. Previously it was
        // disabled until the service restarted, so a device that was off (or slow to answer
        // on Wi-Fi) at startup stayed unreachable (see upstream issue #191, DP-150 over TCP).
        protected static readonly TimeSpan RetryAfterTimeout = TimeSpan.FromSeconds(30);

        private readonly object channelsLock = new object();
        private readonly IDictionary<string, TcpTransport.Channel> openedChannels =
            new Dictionary<string, TcpTransport.Channel>();
        private readonly IDictionary<string, DateTime> failedAddresses =
            new Dictionary<string, DateTime>();

        public override IChannel OpenChannel(string address)
        {
            lock (channelsLock)
            {
                if (openedChannels.TryGetValue(address, out Channel? channel))
                {
                    return channel;
                }
                if (failedAddresses.TryGetValue(address, out var failedAt)
                    && DateTime.Now - failedAt < RetryAfterTimeout)
                {
                    throw new TimeoutException($"{address} disabled due to timeout, retrying after {failedAt + RetryAfterTimeout:HH:mm:ss}");
                }
                try
                {
                    var (hostName, port) = ParseAddress(address);
                    channel = new Channel(hostName, port);
                    openedChannels[address] = channel;
                    failedAddresses.Remove(address);
                    return channel;
                }
                catch (TimeoutException)
                {
                    failedAddresses[address] = DateTime.Now;
                    throw;
                }
            }
        }

        public override void Drop(IChannel channel)
        {
            ((Channel)channel).Close();
        }

        protected (string, int) ParseAddress(string address)
        {
            var parts = address.Split(':');
            if (parts.Length == 1) return (address, DefaultPort);
            var hostName = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : DefaultPort;
            return (hostName, port);
        }

        public class Channel : IChannel
        {
            // TcpClient defaults are 0 / infinite: the connect waited only 200 ms and a read
            // waited forever when the device did not answer, which hung the probing thread.
            protected const int ConnectTimeoutMs = 3000;
            protected const int ReadTimeoutMs = 2000;
            protected const int WriteTimeoutMs = 2000;

            private TcpClient tcpClient;
            private NetworkStream netStream;
            // A read that timed out is still pending on the socket. It must be awaited by the
            // next Read; starting a second ReadAsync on the same stream would lose those bytes.
            private System.Threading.Tasks.Task<int>? pendingRead;
            private byte[] pendingBuffer = Array.Empty<byte>();
            private bool closed;

            private string HostName { get; }
            private int Port { get; }

            public string Descriptor => $"{HostName}:{Port}";

            public Channel(string hostName, int port)
            {
                HostName = hostName;
                Port = port;
                tcpClient = new TcpClient();
                netStream = ConnectAndGetStream();
            }

            protected NetworkStream ConnectAndGetStream()
            {
                pendingRead = null;
                tcpClient.NoDelay = true;
                tcpClient.ReceiveTimeout = ReadTimeoutMs;
                tcpClient.SendTimeout = WriteTimeoutMs;
                var task = tcpClient.ConnectAsync(HostName, Port);
                bool connected;
                try
                {
                    connected = task.Wait(ConnectTimeoutMs);
                }
                catch (AggregateException ex)
                {
                    var errorMessage = $"Cannot connect to {HostName}:{Port}: {ex.InnerException?.Message ?? ex.Message}";
                    Log.Error(errorMessage);
                    throw new TimeoutException(errorMessage, ex);
                }
                if (connected)
                {
                    closed = false;
                    var stream = tcpClient.GetStream();
                    stream.ReadTimeout = ReadTimeoutMs;
                    stream.WriteTimeout = WriteTimeoutMs;
                    return stream;
                }
                var errorMessage = $"Timeout occured while connecting to {HostName}:{Port}";
                Log.Error(errorMessage);
                throw new TimeoutException(errorMessage);
            }

            public void Dispose()
            {
                Close();
            }

            public void Close()
            {
                closed = true;
                pendingRead = null;
                tcpClient.Close();
                // Closing the tcpClient instance does not close the network stream.
                netStream.Close();
            }

            /// <summary>
            /// Reads data from the tcp connection.
            /// </summary>
            /// <returns>The data which was read.</returns>
            public byte[] Read()
            {
                if (pendingRead == null)
                {
                    pendingBuffer = new byte[tcpClient.ReceiveBufferSize];
                    pendingRead = netStream.ReadAsync(pendingBuffer, 0, pendingBuffer.Length);
                }
                var task = pendingRead;
                bool completed;
                try
                {
                    completed = task.Wait(ReadTimeoutMs);
                }
                catch (AggregateException ex)
                {
                    pendingRead = null;
                    var message = $"Connection to {HostName}:{Port} failed while reading: {ex.InnerException?.Message ?? ex.Message}";
                    Log.Error(message);
                    CloseQuietly();
                    throw new System.IO.IOException(message, ex);
                }
                if (completed)
                {
                    pendingRead = null;
                    if (task.Result == 0)
                    {
                        // The device closed the connection. The next Write reconnects.
                        var message = $"Connection closed by {HostName}:{Port}";
                        Log.Warning(message);
                        CloseQuietly();
                        throw new System.IO.IOException(message);
                    }
                    var result = new byte[task.Result];
                    Array.Copy(pendingBuffer, result, task.Result);
                    return result;
                }
                var errorMessage = $"Timeout occured while reading from tcp connection {HostName}:{Port}";
                Log.Error(errorMessage);
                throw new TimeoutException(errorMessage);
            }

            private void CloseQuietly()
            {
                try { Close(); } catch { }
            }

            private void Reconnect()
            {
                try
                {
                    Close();
                }
                finally
                {
                    // Closing TcpClient disposes instance
                    tcpClient = new TcpClient();
                    netStream = ConnectAndGetStream();
                }
            }

            /// <summary>
            /// Writes the specified data to the tcp connection.
            /// </summary>
            /// <param name="data">The data to write.</param>
            public void Write(byte[] data)
            {
                if (closed || !tcpClient.Connected)
                {
                    try
                    {
                        Reconnect();
                    } 
                    catch (Exception inner)
                    {
                        var errorMessage = $"Cannot reconnect to {HostName}:{Port}";
                        Log.Error(errorMessage);
                        throw new TimeoutException(errorMessage, inner);
                    }
                }
                var bytesToWrite = data.Length;
                while (bytesToWrite > 0)
                {
                    var writeSize = Math.Min(bytesToWrite, tcpClient.SendBufferSize);
                    var task = netStream.WriteAsync(
                        data,
                        data.Length - bytesToWrite,
                        writeSize
                    );
                    if (task.Wait(WriteTimeoutMs))
                    {
                        bytesToWrite -= writeSize;
                    }
                    else
                    {
                        var errorMessage = $"Timeout occured while writing to tcp connection {HostName}:{Port}";
                        Log.Error(errorMessage);
                        throw new TimeoutException(errorMessage);
                    }
                }
            }
        }

    }
}