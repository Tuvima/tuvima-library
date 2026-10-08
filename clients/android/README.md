# Tuvima Android clients

This Gradle workspace contains the shared API/authentication layer, the Android
TV pilot, the phone/tablet application, and the Android Auto media service.

Requirements: JDK 17, Android SDK 37, Gradle 9.3.1, and an Android device or
emulator. CI installs the pinned toolchain. This Windows checkout does not
currently have a local Android toolchain.

Build from this directory with `gradle :tv:assembleDebug :mobile:assembleDebug`.
Never place signing keys, service credentials, bearer tokens, or signed media
URLs in this repository.
