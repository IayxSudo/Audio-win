import Foundation
import SwiftUI
import UIKit

public struct AccentColorPreset: Identifiable, Hashable {
    public var id: String { name }
    public let name: String
    public let primaryHex: String
    public let secondaryHex: String
    
    public var primaryColor: Color {
        Color(hex: primaryHex)
    }
    
    public var secondaryColor: Color {
        Color(hex: secondaryHex)
    }
    
    public var gradient: LinearGradient {
        LinearGradient(
            colors: [primaryColor, secondaryColor],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
    }
}

public final class ThemeManager: ObservableObject {
    public static let shared = ThemeManager()
    
    public static let accents: [AccentColorPreset] = [
        AccentColorPreset(name: "Violet", primaryHex: "#7C5CFF", secondaryHex: "#B65CFF"),
        AccentColorPreset(name: "Ocean",  primaryHex: "#2E9BFF", secondaryHex: "#22D3EE"),
        AccentColorPreset(name: "Ember",  primaryHex: "#FF7A45", secondaryHex: "#FF4D8D"),
        AccentColorPreset(name: "Mint",   primaryHex: "#22C88A", secondaryHex: "#7CE38B"),
        AccentColorPreset(name: "Rose",   primaryHex: "#FF5C8A", secondaryHex: "#FF9A6C"),
        AccentColorPreset(name: "Gold",   primaryHex: "#F5A524", secondaryHex: "#FFD466"),
    ]
    
    @Published public var currentAccentName: String = "Violet"
    @Published public var isDarkMode: Bool = true
    
    public var currentAccent: AccentColorPreset {
        ThemeManager.accents.first(where: { $0.name == currentAccentName }) ?? ThemeManager.accents[0]
    }
    
    public var accentPrimary: Color {
        currentAccent.primaryColor
    }
    
    public var accentSecondary: Color {
        currentAccent.secondaryColor
    }
    
    public var accentGradient: LinearGradient {
        currentAccent.gradient
    }
    
    // Theme Palettes
    public var background: Color {
        isDarkMode ? Color(hex: "#050508") : Color(hex: "#F8F9FA")
    }
    
    public var surface: Color {
        isDarkMode ? Color(hex: "#101014") : Color(hex: "#FFFFFF")
    }
    
    public var surfaceElevated: Color {
        isDarkMode ? Color(hex: "#18181E") : Color(hex: "#F1F3F5")
    }
    
    public var surfaceOverlay: Color {
        isDarkMode ? Color(hex: "#22222B") : Color(hex: "#E9ECEF")
    }
    
    public var stroke: Color {
        isDarkMode ? Color(hex: "#262630") : Color(hex: "#DEE2E6")
    }
    
    public var textPrimary: Color {
        isDarkMode ? Color.white : Color(hex: "#111111")
    }
    
    public var textSecondary: Color {
        isDarkMode ? Color(hex: "#A0A0B0") : Color(hex: "#6C757D")
    }
    
    public var textDim: Color {
        isDarkMode ? Color(hex: "#606070") : Color(hex: "#ADB5BD")
    }
    
    public func setAccent(name: String) {
        if ThemeManager.accents.contains(where: { $0.name == name }) {
            currentAccentName = name
        }
    }
}

// MARK: - Color Hex Extension
extension Color {
    public init(hex: String) {
        let hex = hex.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
        var int: UInt64 = 0
        Scanner(string: hex).scanHexInt64(&int)
        let a, r, g, b: UInt64
        switch hex.count {
        case 3: // RGB (12-bit)
            (a, r, g, b) = (255, (int >> 8) * 17, (int >> 4 & 0xF) * 17, (int & 0xF) * 17)
        case 6: // RGB (24-bit)
            (a, r, g, b) = (255, int >> 16, int >> 8 & 0xFF, int & 0xFF)
        case 8: // ARGB (32-bit)
            (a, r, g, b) = (int >> 24, int >> 16 & 0xFF, int >> 8 & 0xFF, int & 0xFF)
        default:
            (a, r, g, b) = (255, 124, 92, 255)
        }

        self.init(
            .sRGB,
            red: Double(r) / 255,
            green: Double(g) / 255,
            blue: Double(b) / 255,
            opacity: Double(a) / 255
        )
    }
}

// MARK: - Glassmorphism View Modifier
public struct GlassmorphicCard: ViewModifier {
    @ObservedObject var theme = ThemeManager.shared
    var cornerRadius: CGFloat = 16
    var isElevated: Bool = false
    
    public func body(content: Content) -> some View {
        content
            .background(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .fill(isElevated ? theme.surfaceElevated.opacity(0.85) : theme.surface.opacity(0.75))
                    .background(
                        BlurView(style: theme.isDarkMode ? .systemMaterialDark : .systemMaterialLight)
                            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                    )
            )
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .stroke(theme.stroke.opacity(0.4), lineWidth: 1)
            )
            .shadow(color: Color.black.opacity(theme.isDarkMode ? 0.3 : 0.08), radius: 10, x: 0, y: 5)
    }
}

public struct BlurView: UIViewRepresentable {
    public var style: UIBlurEffect.Style
    
    public func makeUIView(context: Context) -> UIVisualEffectView {
        UIVisualEffectView(effect: UIBlurEffect(style: style))
    }
    
    public func updateUIView(_ uiView: UIVisualEffectView, context: Context) {
        uiView.effect = UIBlurEffect(style: style)
    }
}

extension View {
    public func glassCard(cornerRadius: CGFloat = 16, isElevated: Bool = false) -> some View {
        self.modifier(GlassmorphicCard(cornerRadius: cornerRadius, isElevated: isElevated))
    }
}
