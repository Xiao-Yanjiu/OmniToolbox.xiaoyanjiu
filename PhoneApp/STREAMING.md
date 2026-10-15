# Streaming implementation notes

The optional hardware path captures only the FFXIV window with Windows Graphics Capture. The selected DXGI adapter creates the capture D3D11 device and FFmpeg D3D11 hardware device. Hardware encoders: NVIDIA h264_nvenc, AMD h264_amf, Intel h264_qsv. No software encoder is selected automatically. Since 0.3.58, the default GPU mode uses FFmpeg 8.1 gfxcapture with the validated FFXIV HWND and D3D11 textures. NVIDIA textures are passed directly to NVENC without the previous application CPU readback/raw pixel pipe. AMD AMF and Intel QSV paths are implemented but have not been tested on physical AMD/Intel hardware. The explicit CPU compatibility option retains the older readback path; there is no silent software encoder fallback. This is not a claim of zero copies inside drivers.

FFmpeg produces Annex-B H.264, no B frames, one-second IDR intervals, inserted AUD delimiters. The helper frames complete access units over stdout; pipe read boundaries are not video boundaries. The module sends video over authenticated encrypted UDP by default in 0.3.58; explicit TCP compatibility remains available. HTTPS carries authentication, configuration and reliable key/button state. A two-packet queue clears on overflow and suppresses dependent pictures until the next IDR. A slow write times out rather than building unlimited latency. Android reads a continuous binary response, feeds a hardware MediaCodec, sets KEY_LOW_LATENCY on Android 11+, drains output and renders the newest available frame immediately. It does not request JPEG images for this path.

This is not the Sunshine/GameStream wire protocol and is not represented as equivalent to Moonlight latency. The TCP compatibility path retains head-of-line blocking; CPU compatibility retains readback. Device-specific decoder behavior and source rendering rate remain limitations. Native Android layout and real network latency must be verified on the phone.

## References and licenses

No source code from Sunshine or Moonlight was copied into this implementation. Their architecture informed hardware encoding, bounded queues, no frame reordering and immediate Android decoding:

- Sunshine (GPL-3.0): https://github.com/LizardByte/Sunshine/blob/master/src/video.cpp
- Sunshine license: https://github.com/LizardByte/Sunshine/blob/master/LICENSE
- Moonlight Android (GPL-3.0): https://github.com/moonlight-stream/moonlight-android/blob/master/app/src/main/java/com/limelight/binding/video/MediaCodecDecoderRenderer.java
- Moonlight license: https://github.com/moonlight-stream/moonlight-android/blob/master/LICENSE.txt
- Android MediaCodec: https://developer.android.com/reference/android/media/MediaCodec
- FFmpeg hardware devices/encoding: https://ffmpeg.org/ffmpeg.html
- NVIDIA FFmpeg hardware acceleration: https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/pdf/Using_FFmpeg_with_NVIDIA_GPU_Hardware_Acceleration.pdf

FFmpeg is a separately installed executable, not bundled in this release. The recommended fixed LGPL shared build and checksum are in RELEASE-0.3.51.md (FFmpeg; unchanged in 0.3.53). Users explicitly select local component paths and enable hardware streaming; it is disabled by default. Build sources/recipes: https://github.com/BtbN/FFmpeg-Builds and https://github.com/FFmpeg/FFmpeg/tree/46d899d7a0 . The hardware helper shipped by this project is GPL-3.0-or-later and its complete corresponding source is in PhoneApp/stream-host.

Version 0.3.53 removes character model preview. Window capture uses a high-resolution phase clock, a latest-frame channel and pooled CPU pixel buffers. Video can be scaled to a maximum of 3840x2160. This is still not a zero-copy GPU capture pipeline.

## PCU1 UDP transport (0.3.58)

Default service port: UDP 18766. Existing HTTPS/TCP 18765 stays unchanged. For Internet access, forward the selected external UDP port to the service computer UDP port and permit it in the firewall. LAN use does not need router forwarding. A TCP-only tunnel cannot carry this UDP flow: configure the separate UDP hostname/port in the App or select TCP compatibility. Current UDP implementation uses IPv4 and does not implement ICE/STUN/TURN or automatic router mapping.

A fresh AES-256-GCM key is issued by the authenticated, certificate-pinned HTTPS session for every native receiver connection, including retries. Keys never enter JavaScript. Direction-specific nonce prefixes plus monotonic packet sequences prevent nonce reuse. A three-way encrypted challenge confirms endpoint return reachability before video transmission. Replayed control packets cannot refresh authority. Revocation, disposal and heartbeat expiry release remote input and close the session.

Datagrams are at most 1200 bytes: 32-byte authenticated metadata, up to 1152 encrypted data bytes and 16-byte tag. One encrypted XOR parity datagram per eight fragments repairs one missing fragment per group. More loss discards the damaged prediction chain until an IDR. Assemblies and frame queues are bounded; there is no video retransmission. The client keeps a bounded replay window for out-of-order packets. Video pacing includes parity/header overhead, so selected encoder bitrate is lower than total wire bitrate. Sustained reported loss reduces 20M to 8M to 2M via a fresh encoder IDR; it does not change the selected preference. Native decoding uses the same MediaCodec path for UDP and TCP.

Tests include .NET/Java crypto interoperability, real UDP handshake, replay/tamper rejection, single-fragment recovery, irrecoverable frame handling, actual captured H264 reception and FFmpeg decode. Physical Android and Internet end-to-end latency remain unmeasured. Local RTX 3090 Ti window tests at 2560x1368/target60/8M produced about 48 FPS in both CPU and GPU modes. GPU mode reduced first-frame startup in this short sample (643ms to 418ms), which is not input-to-display latency. Runtime 8M-to-2M switching continued to produce frames. Do not infer a universal FPS or latency gain from these measurements.
