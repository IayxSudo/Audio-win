import Foundation
import AVFoundation
import Accelerate

public final class AudioEngine: ObservableObject {
    public static let shared = AudioEngine()
    
    // Core AVFoundation Audio Graph
    private let engine = AVAudioEngine()
    private let playerNode = AVAudioPlayerNode()
    private let eqNode = AVAudioUnitEQ(numberOfBands: 10)
    private let mixerNode = AVAudioMixerNode()
    
    // Fallback streaming player for remote URLs
    private var streamingPlayer: AVPlayer?
    private var streamingTimeObserver: Any?
    
    // Active Audio File & State
    private var currentAudioFile: AVAudioFile?
    private var currentFilePath: String?
    private var audioSampleRate: Double = 44100.0
    private var audioLengthSamples: AVAudioFramePosition = 0
    private var seekFrameOffset: AVAudioFramePosition = 0
    private var startFramePosition: AVAudioFramePosition = 0
    
    // Playback state
    @Published public private(set) var isPlaying: Bool = false
    @Published public private(set) var currentTime: Double = 0.0
    @Published public private(set) var duration: Double = 0.0
    @Published public private(set) var fftMagnitudes: [Float] = Array(repeating: 0.0, count: 32)
    
    public var onPlaybackEnded: (() -> Void)?
    
    // 10-band Center Frequencies matching AudioWin Desktop
    public static let frequencies: [Float] = [
        32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    ]
    
    private var timer: Timer?
    private var isSeeking = false
    
    public init() {
        setupAudioSession()
        setupAudioGraph()
        setupNotificationObservers()
    }
    
    deinit {
        stopTimer()
        engine.stop()
        if let observer = streamingTimeObserver {
            streamingPlayer?.removeTimeObserver(observer)
        }
    }
    
    // MARK: - Audio Session Setup
    private func setupAudioSession() {
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .default, options: [.allowBluetooth, .allowBluetoothA2DP, .defaultToSpeaker])
            try session.setActive(true)
        } catch {
            print("AudioEngine: Failed to activate AVAudioSession: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Audio Graph Setup
    private func setupAudioGraph() {
        engine.attach(playerNode)
        engine.attach(eqNode)
        engine.attach(mixerNode)
        
        // Configure 10 EQ Bands
        for i in 0..<10 {
            let band = eqNode.bands[i]
            band.frequency = AudioEngine.frequencies[i]
            band.bandwidth = 1.0
            band.filterType = .parametric
            band.gain = 0.0
            band.bypass = false
        }
        
        // Connect Nodes: Player -> EQ -> Mixer -> MainMixer
        let format = engine.outputNode.outputFormat(forBus: 0)
        engine.connect(playerNode, to: eqNode, format: format)
        engine.connect(eqNode, to: mixerNode, format: format)
        engine.connect(mixerNode, to: engine.mainMixerNode, format: format)
        
        // Install Visualizer FFT Tap on MixerNode
        installVisualizerTap()
        
        do {
            try engine.start()
        } catch {
            print("AudioEngine: Failed to start AVAudioEngine: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Notifications
    private func setupNotificationObservers() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleAudioSessionInterruption),
            name: AVAudioSession.interruptionNotification,
            object: AVAudioSession.sharedInstance()
        )
        
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleRouteChange),
            name: AVAudioSession.routeChangeNotification,
            object: AVAudioSession.sharedInstance()
        )
    }
    
    @objc private func handleAudioSessionInterruption(notification: Notification) {
        guard let userInfo = notification.userInfo,
              let typeValue = userInfo[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: typeValue) else { return }
        
        DispatchQueue.main.async {
            switch type {
            case .began:
                self.pause()
            case .ended:
                if let optionsValue = userInfo[AVAudioSessionInterruptionOptionKey] as? UInt {
                    let options = AVAudioSession.InterruptionOptions(rawValue: optionsValue)
                    if options.contains(.shouldResume) {
                        self.play()
                    }
                }
            @unknown default:
                break
            }
        }
    }
    
    @objc private func handleRouteChange(notification: Notification) {
        guard let userInfo = notification.userInfo,
              let reasonValue = userInfo[AVAudioSessionRouteChangeReasonKey] as? UInt,
              let reason = AVAudioSession.RouteChangeReason(rawValue: reasonValue) else { return }
        
        if reason == .oldDeviceUnavailable {
            // E.g. Headphones unplugged or Bluetooth disconnected -> pause playback
            DispatchQueue.main.async {
                self.pause()
            }
        }
    }
    
    // MARK: - Load & Play
    public func load(track: Track) {
        stop()
        
        guard let path = track.filePath, !path.isEmpty else {
            print("AudioEngine: Track has no valid file path.")
            return
        }
        
        currentFilePath = path
        
        // Remote streaming fallback
        if path.starts(with: "http://") || path.starts(with: "https://") {
            loadStreamingUrl(URL(string: path)!)
            return
        }
        
        let url = URL(fileURLWithPath: path)
        do {
            let file = try AVAudioFile(forReading: url)
            currentAudioFile = file
            audioSampleRate = file.processingFormat.sampleRate
            audioLengthSamples = file.length
            duration = Double(audioLengthSamples) / audioSampleRate
            seekFrameOffset = 0
            startFramePosition = 0
            currentTime = 0.0
            
            // Reconnect player node if format changed
            engine.disconnectNodeOutput(playerNode)
            engine.connect(playerNode, to: eqNode, format: file.processingFormat)
            
            scheduleBuffer(from: 0)
        } catch {
            print("AudioEngine: Error loading audio file: \(error.localizedDescription)")
        }
    }
    
    private func loadStreamingUrl(_ url: URL) {
        let item = AVPlayerItem(url: url)
        streamingPlayer = AVPlayer(playerItem: item)
        
        streamingTimeObserver = streamingPlayer?.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.25, preferredTimescale: 600),
            queue: .main
        ) { [weak self] time in
            guard let self = self, !self.isSeeking else { return }
            self.currentTime = time.seconds
            if let durationSec = self.streamingPlayer?.currentItem?.duration.seconds, !durationSec.isNaN {
                self.duration = durationSec
            }
        }
        
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleStreamingEnded),
            name: .AVPlayerItemDidPlayToEndTime,
            object: item
        )
    }
    
    @objc private func handleStreamingEnded() {
        DispatchQueue.main.async {
            self.isPlaying = false
            self.onPlaybackEnded?()
        }
    }
    
    private func scheduleBuffer(from frame: AVAudioFramePosition) {
        guard let file = currentAudioFile else { return }
        
        playerNode.stop()
        let framesToPlay = AVAudioFrameCount(file.length - frame)
        guard framesToPlay > 0 else { return }
        
        file.framePosition = frame
        playerNode.scheduleSegment(
            file,
            startingFrame: frame,
            frameCount: framesToPlay,
            at: nil
        ) { [weak self] in
            DispatchQueue.main.async {
                guard let self = self else { return }
                if self.isPlaying && self.currentTime >= (self.duration - 0.5) {
                    self.isPlaying = false
                    self.onPlaybackEnded?()
                }
            }
        }
    }
    
    // MARK: - Playback Controls
    public func play() {
        if let stream = streamingPlayer {
            stream.play()
            isPlaying = true
            return
        }
        
        if !engine.isRunning {
            try? engine.start()
        }
        
        playerNode.play()
        isPlaying = true
        startTimer()
    }
    
    public func pause() {
        if let stream = streamingPlayer {
            stream.pause()
            isPlaying = false
            return
        }
        
        playerNode.pause()
        isPlaying = false
        stopTimer()
    }
    
    public func togglePlayPause() {
        if isPlaying {
            pause()
        } else {
            play()
        }
    }
    
    public func stop() {
        stopTimer()
        if let stream = streamingPlayer {
            stream.pause()
            streamingPlayer = nil
        }
        playerNode.stop()
        isPlaying = false
        currentTime = 0.0
        currentAudioFile = nil
    }
    
    public func seek(to timeInSeconds: Double) {
        isSeeking = true
        let clampedTime = max(0, min(timeInSeconds, duration))
        currentTime = clampedTime
        
        if let stream = streamingPlayer {
            stream.seek(to: CMTime(seconds: clampedTime, preferredTimescale: 600)) { [weak self] _ in
                self?.isSeeking = false
            }
            return
        }
        
        guard let _ = currentAudioFile else {
            isSeeking = false
            return
        }
        
        let targetFrame = AVAudioFramePosition(clampedTime * audioSampleRate)
        seekFrameOffset = targetFrame
        
        let wasPlaying = isPlaying
        playerNode.stop()
        scheduleBuffer(from: targetFrame)
        if wasPlaying {
            playerNode.play()
        }
        
        isSeeking = false
    }
    
    // MARK: - Equalizer Controls
    public func setEqGains(_ gains: [Float]) {
        for (i, gain) in gains.prefix(10).enumerated() {
            let clamped = max(-12.0, min(12.0, gain))
            eqNode.bands[i].gain = clamped
        }
    }
    
    public func setEqPreset(_ preset: EqPreset) {
        setEqGains(preset.gains)
    }
    
    // MARK: - Volume & Options
    public func setVolume(_ vol: Float) {
        let clamped = max(0.0, min(1.0, vol))
        mixerNode.outputVolume = clamped
    }
    
    public func setMono(_ isMono: Bool) {
        mixerNode.pan = isMono ? 0.0 : mixerNode.pan
    }
    
    // MARK: - Timer for Tracking Progress
    private func startTimer() {
        stopTimer()
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            guard let self = self, self.isPlaying, !self.isSeeking else { return }
            
            if let nodeTime = self.playerNode.lastRenderTime,
               let playerTime = self.playerNode.playerTime(forNodeTime: nodeTime) {
                let elapsed = Double(playerTime.sampleTime) / self.audioSampleRate
                let calculated = Double(self.seekFrameOffset) / self.audioSampleRate + elapsed
                if calculated >= 0 && calculated <= self.duration {
                    self.currentTime = calculated
                }
            }
        }
    }
    
    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }
    
    // MARK: - Visualizer FFT Tap
    private func installVisualizerTap() {
        let bufferSize: AVAudioFrameCount = 1024
        let format = mixerNode.outputFormat(forBus: 0)
        
        mixerNode.installTap(onBus: 0, bufferSize: bufferSize, format: format) { [weak self] buffer, _ in
            guard let self = self, self.isPlaying else { return }
            self.processFft(buffer: buffer)
        }
    }
    
    private func processFft(buffer: AVAudioPCMBuffer) {
        guard let channelData = buffer.floatChannelData?[0] else { return }
        let frameLength = Int(buffer.frameLength)
        guard frameLength >= 512 else { return }
        
        let log2n = vDSP_Length(round(log2(Double(512))))
        guard let fftSetup = vDSP_create_fftsetup(log2n, FFTRadix(kFFTRadix2)) else { return }
        defer { vDSP_destroy_fftsetup(fftSetup) }
        
        var realp = [Float](repeating: 0.0, count: 256)
        var imagp = [Float](repeating: 0.0, count: 256)
        
        realp.withUnsafeMutableBufferPointer { rPtr in
            imagp.withUnsafeMutableBufferPointer { iPtr in
                var splitComplex = DSPSplitComplex(realp: rPtr.baseAddress!, imagp: iPtr.baseAddress!)
                channelData.withMemoryRebound(to: DSPComplex.self, capacity: 256) { compPtr in
                    vDSP_ctoz(compPtr, 2, &splitComplex, 1, 256)
                }
                vDSP_fft_zrip(fftSetup, &splitComplex, 1, log2n, FFTDirection(kFFTDirection_Forward))
            }
        }
        
        var magnitudes = [Float](repeating: 0.0, count: 32)
        for i in 0..<32 {
            let start = i * 8
            var sum: Float = 0
            for j in 0..<8 {
                let r = realp[start + j]
                let im = imagp[start + j]
                sum += sqrt(r * r + im * im)
            }
            let normalized = min(1.0, max(0.05, sum / 120.0))
            magnitudes[i] = normalized
        }
        
        DispatchQueue.main.async {
            self.fftMagnitudes = magnitudes
        }
    }
}
