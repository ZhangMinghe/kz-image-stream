using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Kazyx.ImageStream
{
    public class StreamProcessor
    {
        private const int DEFAULT_REQUEST_TIMEOUT = 5000;

        private ConnectionState state = ConnectionState.Closed;

        public ConnectionState ConnectionState
        {
            get { return state; }
        }

        public event EventHandler Closed;

        protected void OnClosed(EventArgs e)
        {
            if (Closed != null)
            {
                Closed(this, e);
            }
        }

        public delegate void JpegPacketHandler(object sender, JpegEventArgs e);

        public event JpegPacketHandler JpegRetrieved;

        protected void OnJpegRetrieved(JpegEventArgs e)
        {
            if (JpegRetrieved != null)
            {
                JpegRetrieved(this, e);
            }
        }

        public delegate void PlaybackInfoPacketHandler(object sender, PlaybackInfoEventArgs e);

        public event PlaybackInfoPacketHandler PlaybackInfoRetrieved;

        protected void OnPlaybackInfoRetrieved(PlaybackInfoEventArgs e)
        {
            if (PlaybackInfoRetrieved != null)
            {
                PlaybackInfoRetrieved(this, e);
            }
        }

        /// <summary>
        /// Open stream connection for Liveview.
        /// </summary>
        /// <param name="uri">URL to get liveview stream.</param>
        /// <param name="timeout">Timeout to give up establishing connection.</param>
        /// <returns>Connection status as a result. Connected or failed.</returns>
        public async Task<bool> OpenConnection(Uri uri, TimeSpan? timeout = null)
        {
            Log("OpenConnection");
            if (uri == null)
            {
                throw new ArgumentNullException();
            }

            if (state != ConnectionState.Closed)
            {
                return true;
            }

            var tcs = new TaskCompletionSource<bool>();

            state = ConnectionState.TryingConnection;

            var to = (timeout == null) ? TimeSpan.FromMilliseconds(DEFAULT_REQUEST_TIMEOUT) : timeout;

            var request = HttpWebRequest.Create(uri) as HttpWebRequest;
            request.Method = "GET";
            request.AllowReadStreamBuffering = false;
            // request.Headers["Connection"] = "close";

            var streamHandler = new AsyncCallback((ar) =>
            {
                state = ConnectionState.Connected;
                try
                {
                    var req = ar.AsyncState as HttpWebRequest;
                    using (var response = req.EndGetResponse(ar) as HttpWebResponse)
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            Log("Connected Jpeg stream");
                            tcs.TrySetResult(true);

                            var str = response.GetResponseStream(); // Stream will be disposed inside JpegStreamAnalizer.
                            using (var core = new StreamAnalizer(str))
                            {
                                core.RunFpsDetector();
                                core.JpegRetrieved = (packet) => { OnJpegRetrieved(new JpegEventArgs(packet)); };
                                core.PlaybackInfoRetrieved = (packet) => { OnPlaybackInfoRetrieved(new PlaybackInfoEventArgs(packet)); };

                                while (state == ConnectionState.Connected)
                                {
                                    try
                                    {
                                        core.ReadNextPayload();
                                    }
                                    catch (Exception e)
                                    {
                                        Log("Caught " + e.GetType() + ": finish reading loop");
                                        break;
                                    }
                                }
                            }
                            Log("End of reading loop");
                        }
                        else
                        {
                            tcs.TrySetResult(false);
                        }
                    }
                    req.Abort();
                }
                catch (WebException)
                {
                    Log("WebException inside StreamingHandler.");
                    tcs.TrySetResult(false);
                }
                catch (ObjectDisposedException)
                {
                    Log("Caught ObjectDisposedException inside StreamingHandler.");
                }
                catch (IOException)
                {
                    Log("Caught IOException inside StreamingHandler.");
                }
                finally
                {
                    Log("Disconnected Jpeg stream");
                    CloseConnection();
                    OnClosed(new EventArgs());
                }
            });

            request.BeginGetResponse(streamHandler, request);

            StartTimer((int)to.Value.TotalMilliseconds, request);

            return await tcs.Task;
        }

        private async void StartTimer(int to, HttpWebRequest request)
        {
            await Task.Delay(to);
            if (state == ConnectionState.TryingConnection)
            {
                Log("Open request timeout: aborting request.");
                request.Abort();
            }
        }

        /// <summary>
        /// Forcefully close this connection.
        /// </summary>
        public void CloseConnection()
        {
            Log("CloseConnection");
            state = ConnectionState.Closed;
        }

        private static void Log(string message)
        {
            Debug.WriteLine("[LvProcessor] " + message);
        }
    }

    public enum ConnectionState
    {
        Closed,
        TryingConnection,
        Connected
    }

    public class JpegEventArgs : EventArgs
    {
        private readonly JpegPacket packet;

        public JpegEventArgs(JpegPacket packet)
        {
            this.packet = packet;
        }

        public JpegPacket Packet
        {
            get { return packet; }
        }
    }

    public class PlaybackInfoEventArgs : EventArgs
    {
        private readonly PlaybackInfoPacket packet;

        public PlaybackInfoEventArgs(PlaybackInfoPacket packet)
        {
            this.packet = packet;
        }

        public PlaybackInfoPacket Packet
        {
            get { return packet; }
        }
    }
}