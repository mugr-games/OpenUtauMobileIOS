using System.Diagnostics;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using NAudio.Wave;
using OpenUtau.Audio;
using OpenUtau.Core;
using OpenUtauMobile.Utils;
using Serilog;

namespace OpenUtauMobile.Platforms.iOS.Utils.Audio
{
    /// <summary>
    /// iOS playback via AVAudioEngine. Mirrors the Android AudioTrackOutput: a
    /// feeder thread pulls interleaved float samples from the ISampleProvider
    /// and queues small PCM buffers on an AVAudioPlayerNode, blocking while
    /// <see cref="BuffersInFlight"/> buffers are already queued. Nothing that
    /// can block (OpenUtau's mixer waits on rendering) runs on the realtime
    /// audio thread.
    /// </summary>
    public class AVAudioEngineOutput : IAudioOutput
    {
        private const int SampleRate = 44100;
        private const int OutputChannels = 2;
        private const int FramesPerBuffer = 1024; // ~23 ms
        private const int BuffersInFlight = 4;

        private readonly object _lock = new();
        private readonly AVAudioEngine _engine;
        private readonly AVAudioPlayerNode _player;
        private readonly AVAudioFormat _format;
        private readonly bool _isInitialized;

        private ISampleProvider? _sampleProvider;
        private Thread? _feederThread;
        private volatile bool _feeding;
        private SemaphoreSlim _slots = new(BuffersInFlight, BuffersInFlight);

        // Bumped on Init/Stop so completions from buffers that AVAudioPlayerNode
        // flushes on Stop() are not counted toward the new session.
        private int _generation;
        private long _framesPlayed;

        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public int DeviceNumber => 0; // iOS routes output itself.

        public AVAudioEngineOutput()
        {
            _engine = new AVAudioEngine();
            _player = new AVAudioPlayerNode();
            _format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, SampleRate, OutputChannels, false);
            try
            {
                ConfigureAudioSession();
                _engine.AttachNode(_player);
                _engine.Connect(_player, _engine.MainMixerNode, _format);
                _engine.Prepare();
                _isInitialized = true;
                Log.Information($"AVAudioEngineOutput initialized: sampleRate={SampleRate}, channels={OutputChannels}, framesPerBuffer={FramesPerBuffer}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AVAudioEngineOutput initialization failed");
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(ex));
                _isInitialized = false;
            }
        }

        private static void ConfigureAudioSession()
        {
            var session = AVAudioSession.SharedInstance();
            // Playback: audible with the silent switch on, like a music app.
            NSError? error = session.SetCategory(AVAudioSessionCategory.Playback);
            if (error != null)
            {
                Log.Warning($"AVAudioSession.SetCategory failed: {error.LocalizedDescription}");
            }
            error = session.SetActive(true);
            if (error != null)
            {
                Log.Warning($"AVAudioSession.SetActive failed: {error.LocalizedDescription}");
            }
        }

        private bool EnsureEngineRunning()
        {
            if (_engine.Running)
            {
                return true;
            }
            // The engine stops on route changes and interruptions (calls, Siri).
            ConfigureAudioSession();
            if (!_engine.StartAndReturnError(out NSError error))
            {
                Log.Error($"AVAudioEngine start failed: {error?.LocalizedDescription}");
                return false;
            }
            return true;
        }

        public List<AudioOutputDevice> GetOutputDevices()
        {
            return
            [
                new AudioOutputDevice
                {
                    api = "AVAudioEngine",
                    name = "System output",
                    deviceNumber = 0,
                    guid = GuidTools.CreateGuidFromStrings("AVAudioEngine", "default"),
                },
            ];
        }

        public void SelectDevice(Guid guid, int deviceNumber)
        {
        }

        /// <summary>Same unit as the Android/MiniAudio outputs: frames played * 4.</summary>
        public long GetPosition()
        {
            return Interlocked.Read(ref _framesPlayed) * sizeof(float);
        }

        public void Init(ISampleProvider sampleProvider)
        {
            lock (_lock)
            {
                StopInternal();
                _sampleProvider = sampleProvider;
            }
        }

        public void Play()
        {
            lock (_lock)
            {
                if (!_isInitialized || _sampleProvider == null || PlaybackState == PlaybackState.Playing)
                {
                    return;
                }
                if (!EnsureEngineRunning())
                {
                    return;
                }
                _feeding = true;
                _feederThread = new Thread(FeedLoop) { Name = "OpenUtau iOS audio", IsBackground = true };
                _feederThread.Start(_generation);
                _player.Play();
                PlaybackState = PlaybackState.Playing;
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (PlaybackState != PlaybackState.Playing)
                {
                    return;
                }
                StopFeeder();
                _player.Pause(); // Already-queued buffers resume on Play().
                PlaybackState = PlaybackState.Paused;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }

        private void StopInternal()
        {
            StopFeeder();
            Interlocked.Increment(ref _generation);
            try
            {
                _player.Stop(); // Flushes queued buffers; their completions fire now.
            }
            catch (Exception e)
            {
                Debug.WriteLine($"AVAudioPlayerNode stop failed: {e}");
            }
            _slots = new SemaphoreSlim(BuffersInFlight, BuffersInFlight);
            Interlocked.Exchange(ref _framesPlayed, 0);
            PlaybackState = PlaybackState.Stopped;
        }

        private void StopFeeder()
        {
            _feeding = false;
            var thread = _feederThread;
            _feederThread = null;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        private void FeedLoop(object? state)
        {
            int generation = (int)state!;
            ISampleProvider? provider = _sampleProvider;
            SemaphoreSlim slots = _slots;
            if (provider == null)
            {
                return;
            }
            int inChannels = Math.Max(1, provider.WaveFormat.Channels);
            float[] interleaved = new float[FramesPerBuffer * inChannels];
            float[] left = new float[FramesPerBuffer];
            float[] right = new float[FramesPerBuffer];

            try
            {
                while (_feeding)
                {
                    // Wait for a free slot, re-checking _feeding so Pause/Stop never hang.
                    if (!slots.Wait(50))
                    {
                        continue;
                    }
                    int samplesRead = provider.Read(interleaved, 0, interleaved.Length);
                    int frames = samplesRead / inChannels;
                    if (frames <= 0)
                    {
                        slots.Release();
                        Thread.Sleep(5);
                        continue;
                    }
                    for (int i = 0; i < frames; i++)
                    {
                        if (inChannels == 1)
                        {
                            left[i] = right[i] = interleaved[i];
                        }
                        else
                        {
                            left[i] = interleaved[i * inChannels];
                            right[i] = interleaved[i * inChannels + 1];
                        }
                    }

                    var buffer = new AVAudioPcmBuffer(_format, FramesPerBuffer)
                    {
                        FrameLength = (uint)frames,
                    };
                    IntPtr channelData = buffer.FloatChannelData; // float** (non-interleaved)
                    Marshal.Copy(left, 0, Marshal.ReadIntPtr(channelData, 0), frames);
                    Marshal.Copy(right, 0, Marshal.ReadIntPtr(channelData, IntPtr.Size), frames);

                    long queuedFrames = frames;
                    _player.ScheduleBuffer(buffer, () =>
                    {
                        if (Volatile.Read(ref _generation) == generation)
                        {
                            Interlocked.Add(ref _framesPlayed, queuedFrames);
                        }
                        slots.Release();
                        buffer.Dispose();
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "iOS audio feeder thread failed");
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(ex));
            }
        }
    }
}
