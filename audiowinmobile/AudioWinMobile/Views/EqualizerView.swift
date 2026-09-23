import SwiftUI

public struct EqualizerView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    @Environment(\.dismiss) var dismiss
    
    private let labels = ["32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"]
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                VStack(spacing: 24) {
                    // Preset Carousel / Picker
                    VStack(alignment: .leading, spacing: 10) {
                        Text("PRESETS")
                            .font(.system(size: 11, weight: .bold))
                            .tracking(1.5)
                            .foregroundColor(theme.textDim)
                            .padding(.horizontal, 20)
                        
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 10) {
                                ForEach(EqPreset.all) { preset in
                                    let isSelected = playback.settings.eqPreset == preset.name
                                    Button(action: {
                                        playback.applyEqPreset(preset)
                                    }) {
                                        Text(preset.name)
                                            .font(.system(size: 13, weight: isSelected ? .bold : .medium))
                                            .foregroundColor(isSelected ? .white : theme.textPrimary)
                                            .padding(.horizontal, 16)
                                            .padding(.vertical, 8)
                                            .background(
                                                Group {
                                                    if isSelected {
                                                        theme.accentGradient
                                                    } else {
                                                        theme.surfaceElevated
                                                    }
                                                }
                                            )
                                            .cornerRadius(20)
                                            .overlay(
                                                RoundedRectangle(cornerRadius: 20)
                                                    .stroke(theme.stroke.opacity(0.3), lineWidth: 1)
                                            )
                                    }
                                }
                            }
                            .padding(.horizontal, 20)
                        }
                    }
                    .padding(.top, 10)
                    
                    // 10-Band Sliders
                    VStack(spacing: 16) {
                        HStack {
                            Text("+12 dB")
                                .font(.system(size: 10, weight: .bold, design: .monospaced))
                                .foregroundColor(theme.textDim)
                            Spacer()
                            Text("0 dB")
                                .font(.system(size: 10, weight: .bold, design: .monospaced))
                                .foregroundColor(theme.textDim)
                            Spacer()
                            Text("-12 dB")
                                .font(.system(size: 10, weight: .bold, design: .monospaced))
                                .foregroundColor(theme.textDim)
                        }
                        .padding(.horizontal, 24)
                        
                        // Sliders Grid
                        HStack(spacing: 6) {
                            ForEach(0..<10, id: \.self) { i in
                                VerticalEqSlider(
                                    bandIndex: i,
                                    label: labels[i],
                                    gain: Binding(
                                        get: {
                                            if i < playback.settings.eqGains.count {
                                                return playback.settings.eqGains[i]
                                            }
                                            return 0.0
                                        },
                                        set: { newVal in
                                            playback.setEqGain(bandIndex: i, gain: newVal)
                                        }
                                    )
                                )
                            }
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 20)
                        .glassCard(cornerRadius: 20)
                    }
                    .padding(.horizontal, 16)
                    
                    // Reset Button
                    Button(action: {
                        if let flat = EqPreset.all.first(where: { $0.name == "Flat" }) {
                            playback.applyEqPreset(flat)
                        }
                    }) {
                        HStack {
                            Image(systemName: "arrow.counterclockwise")
                            Text("Reset to Flat")
                        }
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundColor(theme.textSecondary)
                        .padding(.horizontal, 24)
                        .padding(.vertical, 12)
                        .background(theme.surfaceElevated)
                        .cornerRadius(24)
                    }
                    
                    Spacer()
                }
            }
            .navigationTitle("Equalizer")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                    .font(.system(size: 16, weight: .bold))
                    .foregroundColor(theme.accentPrimary)
                }
            }
        }
    }
}

// MARK: - Vertical EQ Slider Component
struct VerticalEqSlider: View {
    let bandIndex: Int
    let label: String
    @Binding var gain: Float
    @ObservedObject var theme = ThemeManager.shared
    
    var body: some View {
        VStack(spacing: 8) {
            Text(String(format: "%+.1f", gain))
                .font(.system(size: 9, weight: .bold, design: .monospaced))
                .foregroundColor(gain != 0 ? theme.accentPrimary : theme.textDim)
                .frame(height: 12)
            
            GeometryReader { geo in
                let height = geo.size.height
                // Range -12 to +12 => 0 to 1
                let normalized = CGFloat((gain + 12.0) / 24.0)
                let yOffset = height * (1.0 - normalized)
                
                ZStack(alignment: .bottom) {
                    // Track Line
                    RoundedRectangle(cornerRadius: 3)
                        .fill(theme.surfaceElevated)
                        .frame(width: 4)
                        .frame(maxHeight: .infinity)
                    
                    // Active Fill from Center (0dB)
                    let centerPos = height * 0.5
                    if yOffset < centerPos {
                        // Boosted
                        RoundedRectangle(cornerRadius: 3)
                            .fill(theme.accentGradient)
                            .frame(width: 4, height: centerPos - yOffset)
                            .offset(y: -(height - centerPos))
                    } else {
                        // Cut
                        RoundedRectangle(cornerRadius: 3)
                            .fill(theme.accentPrimary.opacity(0.6))
                            .frame(width: 4, height: yOffset - centerPos)
                            .offset(y: -(height - yOffset))
                    }
                    
                    // Thumb Knob
                    Circle()
                        .fill(Color.white)
                        .frame(width: 20, height: 20)
                        .shadow(color: theme.accentPrimary.opacity(0.6), radius: 4, x: 0, y: 2)
                        .offset(y: -(height - yOffset - 10))
                }
                .frame(maxWidth: .infinity)
                .contentShape(Rectangle())
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { value in
                            let locationY = max(0, min(height, value.location.y))
                            let ratio = 1.0 - (locationY / height)
                            let newGain = Float((ratio * 24.0) - 12.0)
                            gain = max(-12.0, min(12.0, newGain))
                        }
                )
            }
            .frame(height: 160)
            
            Text(label)
                .font(.system(size: 10, weight: .bold))
                .foregroundColor(theme.textSecondary)
        }
    }
}
