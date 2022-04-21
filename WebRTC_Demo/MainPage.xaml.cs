using System;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Microsoft.MixedReality.WebRTC;
using System.Diagnostics;
using Windows.Media.Capture;
using System.Collections.Generic;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace WebRTC_Demo
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private PeerConnection _peerConnection;
        private NodeDssSignaler _signaler;

        private MediaStreamSource _remoteVideoSource;
        private MediaStreamSource _localVideoSource;

        private VideoBridge _localVideoBridge = new VideoBridge(3);
        private VideoBridge _remoteVideoBridge = new VideoBridge(5);

        private object _remoteVideoLock = new object();
        private readonly object _localVideoLock = new object();

        private bool _remoteVideoPlaying = false;
        private bool _localVideoPlaying = false;



        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Application.Current.Suspending += App_Suspending;
        }

        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            if (_peerConnection != null)
            {
                _peerConnection.Close();
                _peerConnection.Dispose();
                _peerConnection = null;
            }

            localVideoPlayerElement.SetMediaPlayer(null);

            if (_signaler != null)
            {
                _signaler.StopPollingAsync();
                _signaler = null;
            }

            remoteVideoPlayerElement.SetMediaPlayer(null);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Create device tracks.
            DeviceAudioTrackSource _microphoneSource;
            DeviceVideoTrackSource _webcamSource;
            LocalAudioTrack _localAudioTrack;
            LocalVideoTrack _localVideoTrack;

            RemoteVideoTrack _remoteVideoTrack;
            RemoteAudioTrack _remoteAudioTrack;

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo
            };

            var capture = new MediaCapture();
            await capture.InitializeAsync(settings);

            // Retrieve list of available caputre devices.
            IReadOnlyList<VideoCaptureDevice> deviceList =
                await DeviceVideoTrackSource.GetCaptureDevicesAsync();

            foreach (VideoCaptureDevice device in deviceList)
                Debugger.Log(0, "", $"Webcam {device.name} (id: {device.id})\n");

            // Establish peer connection.
            _peerConnection = new PeerConnection();
            var config = new PeerConnectionConfiguration
            {
                IceServers = new List<IceServer> {
                    new IceServer{ Urls = { "stun:stun.l.google.com:19302" } }
                }
            };

            await _peerConnection.InitializeAsync(config);

            Debugger.Log(0, "", "Peer connection initialized successfully.\n");

            _peerConnection.LocalSdpReadytoSend += Peer_LocalSdpReadyToSend;
            _peerConnection.IceCandidateReadytoSend += Peer_IceCandidateReadyToSend;
            _peerConnection.Connected += () => 
            {
                Debugger.Log(0, string.Empty, "PeerConnection: connected.\n");
            };

            _peerConnection.IceStateChanged += (IceConnectionState newState) => 
            {
                Debugger.Log(0, string.Empty, $"ICE state: {newState}\n");
            };

            _peerConnection.VideoTrackAdded += (RemoteVideoTrack track) =>
            {
                _remoteVideoTrack = track;
                _remoteVideoTrack.I420AVideoFrameReady += RemoteVideo_I420AFrameReady;
            };

            _webcamSource = await DeviceVideoTrackSource.CreateAsync();
            _webcamSource.I420AVideoFrameReady += LocalI420AFrameReady;

            var videoTrackConfig = new LocalVideoTrackInitConfig { trackName = "webcam_track" };
            _localVideoTrack = LocalVideoTrack.CreateFromSource(_webcamSource, videoTrackConfig);

            _microphoneSource = await DeviceAudioTrackSource.CreateAsync();
            var audioTrackConfig = new LocalAudioTrackInitConfig { trackName = "microphone_track" };
            _localAudioTrack = LocalAudioTrack.CreateFromSource(_microphoneSource, audioTrackConfig);



            CreateOffer.Click += (s,ea) => 
            {
                // Add transceivers.
                Transceiver _audioTransceiver;
                Transceiver _videoTransceiver;

                _audioTransceiver = _peerConnection.AddTransceiver(MediaKind.Audio);
                _videoTransceiver = _peerConnection.AddTransceiver(MediaKind.Video);

                _audioTransceiver.LocalAudioTrack = _localAudioTrack;
                _videoTransceiver.LocalVideoTrack = _localVideoTrack;

                _peerConnection.CreateOffer();
            };
          
            _signaler = new NodeDssSignaler() 
            {
                HttpServerAddress = "http://127.0.0.1:3000/",
                LocalPeerId = "PC1",
                RemotePeerId = "App1"
            };

            _signaler.OnMessage +=
                async (NodeDssSignaler.Message msg) => 
                {
                    switch (msg.MessageType)
                    {
                        case NodeDssSignaler.Message.WireMessageType.Offer:
                            // wait for the offer to be applied
                            await _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
                            // once applied, create an answer
                            _peerConnection.CreateAnswer();
                            break;
                        case NodeDssSignaler.Message.WireMessageType.Answer:
                            await _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
                            break;
                        case NodeDssSignaler.Message.WireMessageType.Ice:
                            _peerConnection.AddIceCandidate(msg.ToIceCandidate());
                            break;
                    }
                };

            _signaler.StartPollingAsync();
        }

        private void RemoteVideo_I420AFrameReady(I420AVideoFrame frame)
        {
            lock (_remoteVideoLock)
            {
                if (!_remoteVideoPlaying)
                {
                    _remoteVideoPlaying = true;
                    uint width = frame.width;
                    uint height = frame.height;
                    RunOnMainThread(() => 
                    {
                        // bridge the remote video track with the remote media player UI
                        int framerate = 30;
                        _remoteVideoSource = CreateI420VideoStreamSource(width, height, framerate);
                        var remoteVideoPlayer = new MediaPlayer
                        {
                            Source = MediaSource.CreateFromMediaStreamSource(_remoteVideoSource)
                        };
                        remoteVideoPlayerElement.SetMediaPlayer(remoteVideoPlayer);
                        remoteVideoPlayer.Play();
                    });
                }
            }
            _remoteVideoBridge.HandleIncomingVideoFrame(frame);
        }

        private void Peer_IceCandidateReadyToSend(IceCandidate candidate)
        {
            var msg = NodeDssSignaler.Message.FromIceCandidate(candidate);
            _signaler.SendMessageAsync(msg);
        }

        private void Peer_LocalSdpReadyToSend(SdpMessage message)
        {
            var msg = NodeDssSignaler.Message.FromSdpMessage(message);
            _signaler.SendMessageAsync(msg);
        }

        private void LocalI420AFrameReady(I420AVideoFrame frame)
        {
            lock (_localVideoLock)
            {
                if (!_localVideoPlaying)
                {
                    _localVideoPlaying = true;

                    // Capture the resolution into local variable useable from the lambda below.
                    uint width = frame.width;
                    uint height = frame.height;

                    // Defer UI-related work to the main UI thread.
                    RunOnMainThread(() => {
                        // Bridge the local video track with the local media player UI.
                        int framerate = 30; // 30 fps is assumed for lack of an actual value.
                        _localVideoSource = CreateI420VideoStreamSource(width, height, framerate);
                        var localVideoPlayer = new MediaPlayer
                        {
                            Source = MediaSource.CreateFromMediaStreamSource(_localVideoSource)
                        };

                        localVideoPlayerElement.SetMediaPlayer(localVideoPlayer);
                        localVideoPlayer.Play();
                    });
                }
            }

            // Enqueue the incoming frame into the video bridge; the media player will
            // later dequeue it as soon as it's ready.
            _localVideoBridge.HandleIncomingVideoFrame(frame);
        }

        private void RunOnMainThread(Windows.UI.Core.DispatchedHandler handler)
        {
            if (Dispatcher.HasThreadAccess)
            {
                handler.Invoke();
            }
            else
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, handler);
            }
        }

        private MediaStreamSource CreateI420VideoStreamSource(uint width, uint height, int framerate)
        {
            if (width == 0) throw new ArgumentException("Invalid zero width for video", "width");
            if (height == 0) throw new ArgumentException("Invalid zero height for video", "height");

            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Iyuv, width, height);
            var videoStreamDesc = new VideoStreamDescriptor(videoProperties);
            videoStreamDesc.EncodingProperties.FrameRate.Numerator = (uint)framerate;
            videoStreamDesc.EncodingProperties.FrameRate.Denominator = 1;
            // Bitrate in bits per second: framerate * frame pixel size * I420=12bpp           
            videoStreamDesc.EncodingProperties.Bitrate = (uint)framerate * width * height * 12;

            var videoStreamSource = new MediaStreamSource(videoStreamDesc) { BufferTime = TimeSpan.Zero };

            videoStreamSource.SampleRequested += OnMediaStreamSourceRequested;
            videoStreamSource.IsLive = true;
            videoStreamSource.CanSeek = false;

            return videoStreamSource;
        }

        private void OnMediaStreamSourceRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            VideoBridge videoBridge;

            if (sender == _localVideoSource)
                videoBridge = _localVideoBridge;
            else if (sender == _remoteVideoSource)
                videoBridge = _remoteVideoBridge;
            else
                return;

            videoBridge.TryServeVideoFrame(args);
        }
    }
}