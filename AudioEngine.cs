using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

namespace AudioWin
{
    public class AudioEngine : IDisposable
    {
        private IWavePlayer outputDevice;
        private AudioFileReader audioFile;
        private SampleAggregator aggregator;
        private BypassMonoProvider monoProvider;
        private VolumeSampleProvider normProvider;
        private EqualizerSampleProvider eqProvider;
        private bool isDisposed;
        
        public EqualizerBand[] EqBands { get; private set; }

        public void SetEqBand(int index, float gain)
        {
            if (EqBands != null && index >= 0 && index < EqBands.Length)
            {
                EqBands[index].Gain = gain;
                UpdateEq();
            }
        }

        public AudioEngine()
        {
            EqBands = new EqualizerBand[]
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

        public event Action<float[]> FftDataAvailable;

        public double CurrentTime => audioFile?.CurrentTime.TotalSeconds ?? 0;
        public double TotalTime => audioFile?.TotalTime.TotalSeconds ?? 0;
        public PlaybackState PlaybackState => outputDevice?.PlaybackState ?? PlaybackState.Stopped;

        public float Volume
        {
            get => outputDevice?.Volume ?? 1.0f;
            set { if (outputDevice != null) outputDevice.Volume = value; }
        }

        // Live Toggles
        private bool isMono;
        public bool IsMono 
        { 
            get => isMono; 
            set { isMono = value; if (monoProvider != null) monoProvider.IsActive = value; } 
        }

        private bool isNormalized;
        public bool IsNormalized 
        { 
            get => isNormalized; 
            set { isNormalized = value; if (normProvider != null) normProvider.Volume = value ? 1.4f : 1.0f; } 
        }

        public string EQPreset { get; set; } = "Flat";

        public void Play(string filePath)
        {
            Stop();
            
            audioFile = new AudioFileReader(filePath);
            
            // 1. Base provider
            ISampleProvider source = audioFile;

            // 2. Mono Wrapper (Always there, but can bypass)
            monoProvider = new BypassMonoProvider(source) { IsActive = IsMono };
            
            // 3. Normalization Wrapper (Live volume adjustment)
            normProvider = new VolumeSampleProvider(monoProvider) { Volume = IsNormalized ? 1.4f : 1.0f };

            // 3.5. Equalizer Wrapper
            eqProvider = new EqualizerSampleProvider(normProvider, EqBands);

            // 4. Aggregator for Visuals
            aggregator = new SampleAggregator(eqProvider);
            aggregator.FftDataAvailable += (s, e) => FftDataAvailable?.Invoke(e.FftData);
            
            outputDevice = new WaveOutEvent { DesiredLatency = 150 };
            outputDevice.Init(aggregator);
            outputDevice.Play();
        }

        public void Pause() => outputDevice?.Pause();
        public void Resume() => outputDevice?.Play();
        public void Stop()
        {
            if (outputDevice != null) { outputDevice.Stop(); outputDevice.Dispose(); outputDevice = null; }
            if (audioFile != null) { audioFile.Dispose(); audioFile = null; }
            monoProvider = null;
            normProvider = null;
        }

        public void SetPosition(double percent)
        {
            if (audioFile != null) { audioFile.Position = (long)(audioFile.Length * (percent / 100.0)); }
        }

        public void Dispose() { if (!isDisposed) { Stop(); isDisposed = true; } }
        
        public void UpdateEq() => eqProvider?.UpdateFilters();
    }

    // A Mono provider that can toggle between Stereo and Mono without rebuilding the chain.
    // Done in-place to avoid allocating a separate sample array inside Read() which would destroy the garbage collector.
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

            // Simple In-Place Stereo to Mono conversion
            for (int n = 0; n < read; n += 2)
            {
                float mid = (buffer[offset + n] + buffer[offset + n + 1]) / 2f;
                buffer[offset + n] = mid;
                buffer[offset + n + 1] = mid;
            }
            return read;
        }
    }

    // 1024-point FFT works perfectly for audio visuals. 512 is too chunky; 2048 causes too much drawing lag on the UI dispatcher. 
    // Hamming window keeps the side lobes clean so our UI waveform visualizer doesn't jitter like crazy.
    public class SampleAggregator : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int fftSize = 1024;
        private readonly float[] fftBuffer;
        private readonly Complex[] fftComplexBuffer;
        private int fftPos;

        public event EventHandler<FftEventArgs> FftDataAvailable;
        public WaveFormat WaveFormat => source.WaveFormat;

        public SampleAggregator(ISampleProvider source)
        {
            this.source = source;
            this.fftBuffer = new float[fftSize];
            this.fftComplexBuffer = new Complex[fftSize];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = source.Read(buffer, offset, count);
            for (int n = 0; n < samplesRead; n++)
            {
                float sample = buffer[offset + n];
                fftBuffer[fftPos] = sample;
                fftComplexBuffer[fftPos].X = (float)(sample * FastFourierTransform.HammingWindow(fftPos, fftSize));
                fftComplexBuffer[fftPos].Y = 0;
                fftPos++;
                if (fftPos >= fftSize)
                {
                    fftPos = 0;
                    FastFourierTransform.FFT(true, (int)Math.Log(fftSize, 2), fftComplexBuffer);
                    float[] result = new float[fftSize / 2];
                    for (int i = 0; i < fftSize / 2; i++)
                    {
                        result[i] = (float)Math.Sqrt(fftComplexBuffer[i].X * fftComplexBuffer[i].X + fftComplexBuffer[i].Y * fftComplexBuffer[i].Y);
                    }
                    FftDataAvailable?.Invoke(this, new FftEventArgs(result));
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

    public class EqualizerBand
    {
        public float Frequency { get; set; }
        public float Gain { get; set; }
        public float Bandwidth { get; set; } = 1.414f; // 1 octave bandwidth for smooth graphic EQ blending
    }

    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly EqualizerBand[] bands;
        private readonly BiQuadFilter[,] filters;

        public WaveFormat WaveFormat => source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, EqualizerBand[] bands)
        {
            this.source = source;
            this.bands = bands;
            filters = new BiQuadFilter[source.WaveFormat.Channels, bands.Length];
            UpdateFilters();
        }

        public void UpdateFilters()
        {
            for (int bandIndex = 0; bandIndex < bands.Length; bandIndex++)
            {
                var band = bands[bandIndex];
                for (int ch = 0; ch < source.WaveFormat.Channels; ch++)
                {
                    filters[ch, bandIndex] = BiQuadFilter.PeakingEQ(source.WaveFormat.SampleRate, band.Frequency, band.Bandwidth, band.Gain);
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = source.Read(buffer, offset, count);
            for (int n = 0; n < samplesRead; n++)
            {
                int ch = n % source.WaveFormat.Channels;
                for (int band = 0; band < bands.Length; band++)
                {
                    buffer[offset + n] = filters[ch, band].Transform(buffer[offset + n]);
                }
            }
            return samplesRead;
        }
    }
}
