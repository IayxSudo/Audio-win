// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AudioWinMobile",
    platforms: [
        .iOS(.v16),
        .macOS(.v13)
    ],
    products: [
        .library(
            name: "AudioWinMobile",
            targets: ["AudioWinMobile"]
        ),
    ],
    dependencies: [
        // Pure Swift standard foundation / AVFoundation used for maximum portability and zero third-party bloat
    ],
    targets: [
        .target(
            name: "AudioWinMobile",
            dependencies: [],
            path: "AudioWinMobile",
            resources: [
                .process("Resources")
            ]
        ),
    ]
)
