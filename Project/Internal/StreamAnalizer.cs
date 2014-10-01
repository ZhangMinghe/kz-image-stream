﻿using Kazyx.ImageStream.FocusInfo;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Kazyx.ImageStream
{
    internal class StreamAnalizer : IDisposable
    {
        private bool _IsOpen = true;
        public bool IsOpen
        {
            private set { _IsOpen = value; }
            get { return _IsOpen; }
        }

        private const int CHeaderLength = 8;
        private const int PHeaderLength = 128;

        private readonly byte[] ReadBuffer = new byte[8192];

        private readonly Stream stream;

        internal Action<JpegPacket> JpegRetrieved;

        protected void OnJpegRetrieved(JpegPacket packet)
        {
            if (JpegRetrieved != null)
            {
                JpegRetrieved.Invoke(packet);
            }
        }

        internal Action<FocusFramePacket> FocusFrameRetrieved;

        protected void OnFrameInfoRetrieved(FocusFramePacket packet)
        {
            if (FocusFrameRetrieved != null)
            {
                FocusFrameRetrieved(packet);
            }
        }

        internal Action<PlaybackInfoPacket> PlaybackInfoRetrieved;

        protected void OnPlaybackInfo(PlaybackInfoPacket packet)
        {
            if (PlaybackInfoRetrieved != null)
            {
                PlaybackInfoRetrieved.Invoke(packet);
            }
        }

        internal StreamAnalizer(Stream stream)
        {
            if (stream == null)
            {
                Log("Stream MUST NOT be null");
                throw new ArgumentNullException("Stream MUST NOT be null.");
            }
            this.stream = stream;
        }

        public void Dispose()
        {
            IsOpen = false;
            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    Log("Ignore ObjectDisposedException");
                }
            }
            JpegRetrieved = null;
            PlaybackInfoRetrieved = null;
            FocusFrameRetrieved = null;
        }

        private const int FPS_INTERVAL = 5000;
        private int packet_counter = 0;

        internal async void RunFpsDetector()
        {
            while (IsOpen)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(FPS_INTERVAL));
                var fps = packet_counter * 1000 / FPS_INTERVAL;
                packet_counter = 0;
                Log("- - - - " + fps + " FPS - - - -");
            }
        }

        /// <summary>
        /// Read next payload from the stream.
        /// Retrieved data will be provided by event delegates.
        /// </summary>
        internal void ReadNextPayload()
        {
            var CHeader = StreamHelper.ReadBytes(stream, CHeaderLength, ReadBuffer, () => { return IsOpen; });
            if (CHeader[0] != (byte)0xFF) // Check fixed data
            {
                Log("Unexpected common header");
                throw new IOException("Unexpected common header");
            }

            switch (CHeader[1])
            {
                case (byte)0x01: // Liveview stream.
                case (byte)0x11: // Movie playback stream.
                    ReadImagePacket();
                    break;
                case (byte)0x02:
                    ReadFocusFramePacket();
                    break;
                case (byte)0x12: // Movie playback information.
                    ReadPlaybackInformationPacket();
                    break;
                default:
                    throw new IOException("Unsupported payload type: " + CHeader[1]);
            }
        }

        private void ReadImagePacket()
        {
            var PHeader = StreamHelper.ReadBytes(stream, PHeaderLength, ReadBuffer, () => { return IsOpen; });
            if (PHeader[0] != (byte)0x24 || PHeader[1] != (byte)0x35 || PHeader[2] != (byte)0x68 || PHeader[3] != (byte)0x79) // Check fixed data
            {
                Log("Unexpected payload header");
                throw new IOException("Unexpected payload header");
            }

            var data_size = StreamHelper.AsInteger(PHeader, 4, 3);
            var padding_size = StreamHelper.AsInteger(PHeader, 7, 1);
            var width = StreamHelper.AsInteger(PHeader, 8, 2);
            var height = StreamHelper.AsInteger(PHeader, 10, 2);

            var data = StreamHelper.ReadBytes(stream, data_size, ReadBuffer, () => { return IsOpen; });
            StreamHelper.ReadBytes(stream, padding_size, ReadBuffer, () => { return IsOpen; }); // discard padding from stream

            var packet = new JpegPacket
            {
                ImageData = data,
                Width = (uint)width,
                Height = (uint)height
            };

            packet_counter++;

            OnJpegRetrieved(packet);
        }

        private const int FrameInfoV1PayloadLength = 16;

        private void ReadFocusFramePacket()
        {
            var PHeader = StreamHelper.ReadBytes(stream, PHeaderLength, ReadBuffer, () => { return IsOpen; });
            if (PHeader[0] != (byte)0x24 || PHeader[1] != (byte)0x35 || PHeader[2] != (byte)0x68 || PHeader[3] != (byte)0x79) // Check fixed data
            {
                Log("Unexpected payload header");
                throw new IOException("Unexpected payload header");
            }

            var data_size = StreamHelper.AsInteger(PHeader, 4, 3);
            var padding_size = StreamHelper.AsInteger(PHeader, 7, 1);

            if (PHeader[8] != (byte)0x01 || PHeader[9] != (byte)0x00) // Only v1.0 is supported.
            {
                StreamHelper.ReadBytes(stream, data_size, ReadBuffer, () => { return IsOpen; });
                StreamHelper.ReadBytes(stream, padding_size, ReadBuffer, () => { return IsOpen; }); // discard padding from stream
                return;
            }

            var count = StreamHelper.AsInteger(PHeader, 10, 2);
            var size = StreamHelper.AsInteger(PHeader, 12, 2);

            var payload = StreamHelper.ReadBytes(stream, data_size, ReadBuffer, () => { return IsOpen; });
            if (payload.Length != FrameInfoV1PayloadLength)
            {
                StreamHelper.ReadBytes(stream, padding_size, ReadBuffer, () => { return IsOpen; }); // discard padding from stream
                return;
            }

            var squares = new List<FocusFrameInfo>();
            for (var i = 0; i < count; i++)
            {
                var offset = i * size;
                var position = new FocusFrameInfo
                {
                    TopLeft_X = StreamHelper.AsInteger(payload, offset, 2),
                    TopLeft_Y = StreamHelper.AsInteger(payload, offset + 2, 2),
                    BottomRight_X = StreamHelper.AsInteger(payload, offset + 4, 2),
                    BottomRight_Y = StreamHelper.AsInteger(payload, offset + 6, 2),
                    Category = (Category)payload[offset + 8],
                    Status = (Status)payload[offset + 9],
                    AdditionalStatus = (AdditionalStatus)payload[offset + 10]
                };
                squares.Add(position);
            }

            StreamHelper.ReadBytes(stream, padding_size, ReadBuffer, () => { return IsOpen; }); // discard padding from stream

            OnFrameInfoRetrieved(new FocusFramePacket
            {
                FocusFrames = squares
            });
        }

        private const int PlaybackInfoLength = 32;

        private void ReadPlaybackInformationPacket()
        {
            var PHeader = StreamHelper.ReadBytes(stream, PHeaderLength, ReadBuffer, () => { return IsOpen; });
            if (PHeader[0] != (byte)0x24 || PHeader[1] != (byte)0x35 || PHeader[2] != (byte)0x68 || PHeader[3] != (byte)0x79) // Check fixed data
            {
                Log("Unexpected payload header");
                throw new IOException("Unexpected payload header");
            }

            var major = StreamHelper.AsInteger(PHeader, 8, 1);
            var minor = StreamHelper.AsInteger(PHeader, 9, 1);

            var PlaybackData = StreamHelper.ReadBytes(stream, PlaybackInfoLength, ReadBuffer, () => { return IsOpen; });

            var duration = StreamHelper.AsInteger(PlaybackData, 0, 4);
            var position = StreamHelper.AsInteger(PlaybackData, 4, 4);

            var info = new PlaybackInfoPacket()
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                CurrentPosition = TimeSpan.FromMilliseconds(position)
            };

            OnPlaybackInfo(info);
        }

        private static void Log(string message)
        {
            Debug.WriteLine("[JpegStreamAnalizer] " + message);
        }
    }
}
