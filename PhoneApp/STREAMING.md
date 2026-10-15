# Streaming implementation notes

The optional hardware path captures only the FFXIV window with Windows Graphics Capture. The selected DXGI adapter creates the capture D3D11 device and FFmpeg D3D11 hardware device. Hardware encoders: NVIDIA h264_nvenc, AMD h264_amf, Intel h264_qsv. No software encoder is selected automatically. The current implementation reads BGRA capture frames back to the CPU before passing them through a one-frame channel to FFmpeg, so it is not a zero-copy GPU pipeline.

FFmpeg produces Annex-B H.264, no B frames, one-second IDR intervals, inserted AUD delimiters. The helper frames complete access units over stdout; pipe read boundaries are not video boundaries. The module transmits them over the existing pinned TLS/authenticated connection. A two-packet queue clears on overflow and suppresses dependent pictures until the next IDR. A slow write times out rather than building unlimited latency. Android reads a continuous binary response, feeds a hardware MediaCodec, sets KEY_LOW_LATENCY on Android 11+, drains output and renders the newest available frame immediately. It does not request JPEG images for this path.

This is not the Sunshine/GameStream wire protocol and is not represented as equivalent to Moonlight latency. TCP head-of-line blocking, window capture readback and device-specific decoder behavior remain limitations. Native Android layout and real network latency must be verified on the phone.

## References and licenses

No source code from Sunshine or Moonlight was copied into this implementation. Their architecture informed hardware encoding, bounded queues, no frame reordering and immediate Android decoding:

- Sunshine (GPL-3.0): https://github.com/LizardByte/Sunshine/blob/master/src/video.cpp
- Sunshine license: https://github.com/LizardByte/Sunshine/blob/master/LICENSE
- Moonlight Android (GPL-3.0): https://github.com/moonlight-stream/moonlight-android/blob/master/app/src/main/java/com/limelight/binding/video/MediaCodecDecoderRenderer.java
- Moonlight license: https://github.com/moonlight-stream/moonlight-android/blob/master/LICENSE.txt
- Android MediaCodec: https://developer.android.com/reference/android/media/MediaCodec
- FFmpeg hardware devices/encoding: https://ffmpeg.org/ffmpeg.html
- NVIDIA FFmpeg hardware acceleration: https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/pdf/Using_FFmpeg_with_NVIDIA_GPU_Hardware_Acceleration.pdf

FFmpeg is a separately installed executable, not bundled in this release. The recommended fixed LGPL shared build and checksum are in RELEASE-0.3.51.md. Users explicitly select local component paths and enable hardware streaming; it is disabled by default. Build sources/recipes: https://github.com/BtbN/FFmpeg-Builds and https://github.com/FFmpeg/FFmpeg/tree/46d899d7a0 . The hardware helper shipped by this project is GPL-3.0-or-later and its complete corresponding source is in PhoneApp/stream-host.
