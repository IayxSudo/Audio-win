using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudioWin
{
    public class AudioEngine : IDisposable
    {
        private IWavePlayer outputDevice;
        private AudioFileReader audioFile;
        private BypassMonoProvider monoProvider;
        private EqualizerSampleProvider eqProvider;
        private SmoothVolumeProvider gainProvider;   // normalisation trim
        private SmoothVolumeProvider masterProvider; // user volume + fades
        private SampleAggregator aggregator;

        private readonly object sync = new object();
        private bool isDisposed;
        private bool suppressStopEvent;
        private int pauseGeneration;

        private float volume = 0.7f;
        private bool isMuted;
        private bool isMono;
        private bool isNormalized;

        public EqualizerBand[] EqBands { get; }

        /// <summary>Raised (on a background thread) when a track reaches its end on its own.</summary>
        public event Action PlaybackEnded;

        /// <summary>Raised with a fresh magnitude spectrum for the visualiser.</summary>
        public event Action<float[]> FftDataAvailable;

        public AudioEngine()
        {
            EqBands = new[]
            {
                new EqualizerBand { Frequency = 32 },
                new EqualizerBand { Frequency = 64 },
                new EqualizerBand { Frequency = 125 },
                new EqualizerBand { Frequency = 250 },
                new EqualizerBand { Frequency = 500 },
                new EqualizerBand { Frequency = 1000 },
                new EqualizerBand { Frequency = 2000 },
                new EqualizerBand { Frequency = 4000 },
                new EqualizerBand { Frequency = 8000 },
                new EqualizerBand { Frequency = 16000 }
            };
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        public double CurrentTime
        {
            get { lock (sync) { try { return audioFile?.CurrentTime.TotalSeconds ?? 0; } catch { return 0; } } }
        }

        public double TotalTime
        {
            get { lock (sync) { try { return audioFile?.TotalTime.TotalSeconds ?? 0; } catch { return 0; } } }
        }

        public bool HasTrack { get { lock (sync) { return audioFile != null; } } }

        public PlaybackState PlaybackState
        {
            get { lock (sync) { return outputDevice?.PlaybackState ?? NAudio.Wave.PlaybackState.Stopped; } }
        }

        /// <summary>
        /// Master volume, 0..1. Applied through a sample provider rather than
        /// IWavePlayer.Volume. The old build set the volume on the output device,
        /// which is recreated for every track, so the level silently jumped back
        /// to 100% each time a new song started.
        /// </summary>
        public float Volume
        {
            get => volume;
            set
            {
                volume = Math.Clamp(value, 0f, 1f);
                ApplyMasterVolume();
            }
        }

        public bool IsMuted
        {
            get => isMuted;
            set { isMuted = value; ApplyMasterVolume(); }
        }

        public bool IsMono
        {
            get => isMono;
            set { isMono = value; lock (sync) { if (monoProvider != null) monoProvider.IsActive = value; } }
        }

        public bool IsNormalized
        {
            get => isNormalized;
            set
            {
                isNormalized = value;
                lock (sync) { if (gainProvider != null) gainProvider.Target = value ? 1.4f : 1.0f; }
            }
        }

        private void ApplyMasterVolume()
        {
            lock (sync)
            {
                if (masterProvider != null)
                    masterProvider.Target = isMuted ? 0f : volume;
            }
        }

        // ------------------------------------------------------------------
        // Transport
        // ------------------------------------------------------------------

        public void Play(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("No file path supplied.");

            lock (sync)
            {
                StopInternal();

                audioFile = new AudioFileReader(filePath);

                ISampleProvider chain = audioFile;
                monoProvider = new BypassMonoProvider(chain) { IsActive = isMono };
                gainProvider = new SmoothVolumeProvider(monoProvider) { Target = isNormalized ? 1.4f : 1.0f, Current = isNormalized ? 1.4f : 1.0f };
                eqProvider = new EqualizerSampleProvider(gainProvider, EqBands);

                // Start silent and ramp up, so beginning a track never clicks.
                masterProvider = new SmoothVolumeProvider(eqProvider)
                {
                    Current = 0f,
                    Target = isMuted ? 0f : volume
                };

                aggregator = new SampleAggregator(masterProvider);
                aggregator.FftDataAvailable += OnFft;

                outputDevice = new WaveOutEvent { DesiredLatency = 140, NumberOfBuffers = 3 };
                outputDevice.PlaybackStopped += OnPlaybackStopped;
                outputDevice.Init(aggregator);
                suppressStopEvent = false;
                Interlocked.Increment(ref pauseGeneration);
                outputDevice.Play();
            }
        }

        /// <summary>Fades out over ~90ms and then pauses, so pausing never pops.</summary>
        public void Pause()
        {
            int gen;
            lock (sync)
            {
                if (outputDevice == null || outputDevice.PlaybackState != NAudio.Wave.PlaybackState.Playing) return;
                if (masterProvider != null) masterProvider.Target = 0f;
                gen = Interlocked.Increment(ref pauseGeneration);
            }

            Task.Delay(90).ContinueWith(_ =>
            {
                lock (sync)
                {
                    if (gen != Volatile.Read(ref pauseGeneration)) return; // resumed in the meantime
                    try { outputDevice?.Pause(); } catch { }
                }
            });
        }

        public void Resume()
        {
            lock (sync)
            {
                if (outputDevice == null) return;
                Interlocked.Increment(ref pauseGeneration);
                if (masterProvider != null)
                {
                    masterProvider.Current = 0f;
                    masterProvider.Target = isMuted ? 0f : volume;
                }
                try { outputDevice.Play(); } catch { }
            }
        }

        public void Stop()
        {
            lock (sync) { StopInternal(); }
        }

        private void StopInternal()
        {
            suppressStopEvent = true;
            Interlocked.Increment(ref pauseGeneration);

            if (outputDevice != null)
            {
                try { outputDevice.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { outputDevice.Stop(); } catch { }
                try { outputDevice.Dispose(); } catch { }
                outputDevice = null;
            }
            if (aggregator != null)
            {
                try { aggregator.FftDataAvailable -= OnFft; } catch { }
                aggregator = null;
            }
            if (audioFile != null)
            {
                try { audioFile.Dispose(); } catch { }
                audioFile = null;
            }

            monoProvider = null;
            eqProvider = null;
            gainProvider = null;
            masterProvider = null;
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // Distinguish "the file ran out" from "we stopped it deliberately".
            bool natural;
            lock (sync) { natural = !suppressStopEvent; }
            if (natural)
            {
                try { PlaybackEnded?.Invoke(); } catch { }
            }
        }

        private void OnFft(object sender, FftEventArgs e) => FftDataAvailable?.Invoke(e.FftData);

        /// <summary>Seek by percentage of the track (0-100).</summary>
        public void SetPosition(double percent)
        {
            lock (sync)
            {
                if (audioFile == null) return;
                try
                {
                    percent = Math.Clamp(percent, 0, 100);
                    long pos = (long)(audioFile.Length * (percent / 100.0));
                    // Snap to a whole sample frame; a misaligned position swaps the
                    // stereo channels and makes the first buffer sound like static.
                    int block = audioFile.WaveFormat.BlockAlign;
                    if (block > 0) pos -= pos % block;
                    audioFile.Position = Math.Clamp(pos, 0, audioFile.Length);

                    // Tiny re-ramp so scrubbing doesn't crackle.
                    if (masterProvider != null) masterProvider.Current = 0f;
                }
                catch { }
            }
        }

        public void SetPositionSeconds(double seconds)
        {
            double total = TotalTime;
            if (total <= 0) return;
            SetPosition(Math.Clamp(seconds, 0, total) / total * 100.0);
        }

        public void SetEqBand(int index, float gain)
        {
            if (index < 0 || index >= EqBands.Length) return;
            EqBands[index].Gain = gain;
            lock (sync) { eqProvider?.UpdateFilters(); }
        }

        public void UpdateEq()
        {
            lock (sync) { eqProvider?.UpdateFilters(); }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            lock (sync) { StopInternal(); }
        }
    }

    // ======================================================================
    // Sample providers
    // ======================================================================

    /// <summary>
    /// Volume stage that glides towards its target instead of jumping, which
    /// removes the zipper noise you otherwise get when dragging the volume slider
    /// and lets us fade in and out around play/pause.
    /// </summary>
    public class SmoothVolumeProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly float step;

        public WaveFormat WaveFormat => source.WaveFormat;
        public float Target { get; set; } = 1f;
        public float Current { get; set; } = 1f;

        public SmoothVolumeProvider(ISampleProvider source)
        {
            this.source = source;
            // ~12ms glide, expressed per sample so it is sample-rate independent.
            int rate = Math.Max(8000, source.WaveFormat.SampleRate);
            step = 1f / (rate * 0.012f);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            int channels = Math.Max(1, WaveFormat.Channels);

            for (int n = 0; n < read; n++)
            {
                if (n % channels == 0)
                {
                    if (Current < Target) Current = Math.Min(Target, Current + step);
                    else if (Current > Target) Current = Math.Max(Target, Current - step);
                }
                buffer[offset + n] *= Current;
            }
            return read;
        }
    }

    /// <summary>Stereo-to-mono fold that can be toggled without rebuilding the chain.</summary>
    public class BypassMonoProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        public WaveFormat WaveFormat => source.WaveFormat;
        public bool IsActive { get; set; }

        public BypassMonoProvider(ISampleProvider source) => this.source = source;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (!IsActive || WaveFormat.Channels != 2) return read;

            // Done in place: allocating a scratch array per Read would churn the GC
            // hundreds of times a second.
            int pairs = read - (read % 2);
            for (int n = 0; n < pairs; n += 2)
            {
                float mid = (buffer[offset + n] + buffer[offset + n + 1]) * 0.5f;
                buffer[offset + n] = mid;
                buffer[offset + n + 1] = mid;
            }
            return read;
        }
    }

    public class EqualizerBand
    {
        public float Frequency { get; set; }
        public float Gain { get; set; }
        public float Bandwidth { get; set; } = 1.0f; // one octave, smooth blending between bands
    }

    /// <summary>Ten-band peaking EQ, one biquad chain per channel.</summary>
    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly EqualizerBand[] bands;
        private readonly BiQuadFilter[,] filters;
        private readonly int channels;
        private long framePosition;
        private volatile bool bypass;

        public WaveFormat WaveFormat => source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, EqualizerBand[] bands)
        {
            this.source = source;
            this.bands = bands;
            channels = Math.Max(1, source.WaveFormat.Channels);
            filters = new BiQuadFilter[channels, bands.Length];
            UpdateFilters();
        }

        public void UpdateFilters()
        {
            bool allFlat = true;
            for (int b = 0; b < bands.Length; b++)
                if (Math.Abs(bands[b].Gain) > 0.01f) { allFlat = false; break; }

            // With every band at 0dB the filters are a no-op, so skip the maths
            // entirely rather than running 20 biquads per sample for nothing.
            bypass = allFlat;
            if (allFlat) return;

            int sampleRate = source.WaveFormat.SampleRate;
            float maxSafeFreq = sampleRate * 0.45f; // Protect against Nyquist foldover distortion

            for (int b = 0; b < bands.Length; b++)
            {
                var band = bands[b];
                float safeFreq = Math.Clamp(band.Frequency, 20f, maxSafeFreq);
                // Proportional constant-Q physics: 1-octave bandwidth (Q = sqrt(2) ~ 1.414f)
                float q = band.Bandwidth > 0 ? band.Bandwidth : 1.0f;

                for (int ch = 0; ch < channels; ch++)
                {
                    filters[ch, b] = BiQuadFilter.PeakingEQ(sampleRate, safeFreq, q, band.Gain);
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (bypass)
            {
                framePosition += read;
                return read;
            }

            for (int n = 0; n < read; n++)
            {
                // Channel derived from an absolute counter. Using `n % channels`
                // assumed every Read starts on a frame boundary, which is not
                // guaranteed and made the left/right filter states swap.
                int ch = (int)((framePosition + n) % channels);
                float sample = buffer[offset + n];
                for (int b = 0; b < bands.Length; b++)
                {
                    var f = filters[ch, b];
                    if (f != null) sample = f.Transform(sample);
                }
                buffer[offset + n] = sample;
            }

            framePosition += read;
            return read;
        }
    }

    /// <summary>
    /// Taps the stream to produce spectrum data for the visualiser.
    /// 1024 points with a Hamming window: 512 is too coarse to look musical and
    /// 2048 pushes more redraws than the UI thread can keep up with.
    /// </summary>
    public class SampleAggregator : ISampleProvider
    {
        private readonly ISampleProvider source;
        private const int FftSize = 1024;
        private const int FftBits = 10; // log2(1024)

        private readonly Complex[] fftBuffer = new Complex[FftSize];
        private readonly float[] magnitudes = new float[FftSize / 2];
        private readonly int channels;

        private int fftPos;
        private float frameAccumulator;
        private int frameSamples;

        public event EventHandler<FftEventArgs> FftDataAvailable;
        public WaveFormat WaveFormat => source.WaveFormat;

        public SampleAggregator(ISampleProvider source)
        {
            this.source = source;
            channels = Math.Max(1, source.WaveFormat.Channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = source.Read(buffer, offset, count);

            for (int n = 0; n < samplesRead; n++)
            {
                // Average the channels into one mono frame first. Feeding the raw
                // interleaved stream to the FFT treats L and R as consecutive
                // samples, which doubles every apparent frequency.
                frameAccumulator += buffer[offset + n];
                if (++frameSamples < channels) continue;

                float mono = frameAccumulator / channels;
                frameAccumulator = 0;
                frameSamples = 0;

                fftBuffer[fftPos].X = (float)(mono * FastFourierTransform.HammingWindow(fftPos, FftSize));
                fftBuffer[fftPos].Y = 0;
                fftPos++;

                if (fftPos >= FftSize)
                {
                    fftPos = 0;
                    FastFourierTransform.FFT(true, FftBits, fftBuffer);
                    for (int i = 0; i < magnitudes.Length; i++)
                    {
                        float re = fftBuffer[i].X, im = fftBuffer[i].Y;
                        magnitudes[i] = (float)Math.Sqrt(re * re + im * im);
                    }
                    // Reuses one array; the consumer copies what it needs.
                    FftDataAvailable?.Invoke(this, new FftEventArgs(magnitudes));
                }
            }

            return samplesRead;
        }
    }

    public class FftEventArgs : EventArgs
    {
        public float[] FftData { get; }
        public FftEventArgs(float[] data) => FftData = data;
    }
}
