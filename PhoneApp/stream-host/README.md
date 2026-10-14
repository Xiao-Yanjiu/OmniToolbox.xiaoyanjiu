# Stream host

Windows.Graphics.Capture captures the ffxiv_dx11 game window by HWND. It does not capture a monitor or the desktop. The component sends JPEG frames only; no audio or input control is included. The server owns authentication and frame access. The helper receives only the game PID, writes framed JPEG bytes to its stdout pipe, and never listens on a network port.

The helper uses the Microsoft .NET runtime and Windows SDK for .NET projections. .NET runtime notices are preserved under licenses/. Windows SDK terms: https://aka.ms/WinSDKLicenseURL. CsWinRT source and MIT license: https://github.com/microsoft/CsWinRT/blob/master/LICENSE.

This initial stream is capped at approximately 10 FPS and 1280 × 1920 encoded pixels. Game-window resolution changes and restoration still require device acceptance testing. Fullscreen, floating and split display are app presentation modes; floating video stays within the application.
