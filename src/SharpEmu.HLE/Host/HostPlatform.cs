// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Process-wide access point for the host platform backend. Static HLE export
/// classes (which cannot receive constructor injection) resolve host primitives
/// through <see cref="Current"/>; injectable components should instead accept an
/// <see cref="IHostPlatform"/> and merely default to this.
/// </summary>
public static class HostPlatform
{
    private static readonly Lazy<IHostPlatform?> Instance = new(Create);
    private static readonly IHostAudioOutput FallbackAudio = new UnsupportedHostAudioOutput();
    private static readonly IHostInput FallbackInput = new NeutralHostInput();

    public static IHostPlatform Current => Instance.Value ?? throw CreateUnsupportedPlatformException();

    /// <summary>
    /// Host audio available to portable HLE exports. Unsupported hosts return a backend
    /// that fails stream creation so callers can use their existing silent-output path.
    /// </summary>
    public static IHostAudioOutput Audio =>
        Instance.Value?.Audio ?? FallbackAudio;

    /// <summary>
    /// Host input available to portable HLE exports. Unsupported hosts expose a neutral,
    /// disconnected input source instead of making otherwise portable exports throw.
    /// </summary>
    public static IHostInput Input =>
        Instance.Value?.Input ?? FallbackInput;

    public static void RequestTimerResolution()
    {
        Instance.Value?.Threading.RequestTimerResolution();
    }

    private static IHostPlatform? Create()
    {
        // Guest code and generated host stubs execute as x86-64. Native ARM64
        // processes must be rejected before reaching the execution backend;
        // x64 processes under emulation report Architecture.X64.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsHostPlatform();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new PosixHostPlatform();
        }

        return null;
    }

    private static PlatformNotSupportedException CreateUnsupportedPlatformException() =>
        new(
            "SharpEmu native guest execution requires an x86-64 process on Windows, Linux, or macOS. " +
            "On Apple Silicon, use the osx-x64 build under Rosetta 2.");

    private sealed class UnsupportedHostAudioOutput : IHostAudioOutput
    {
        public string BackendName => "silent";

        public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate) =>
            throw new PlatformNotSupportedException("No host audio output backend is available.");
    }

    private sealed class NeutralHostInput : IHostInput
    {
        public void EnsureStarted()
        {
        }

        public int GetGamepadStates(Span<HostGamepadState> destination) => 0;

        public string? DescribeConnectedGamepad() => null;

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
        }

        public void ResetLightbar()
        {
        }

        public bool IsHostWindowFocused() => false;

        public bool IsKeyDown(int virtualKey) => false;
    }
}
