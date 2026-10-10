using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.Elevoc;

/// <summary>
/// Records the default Windows capture path with WinMM as mono PCM16, then
/// writes a mono IEEE-FLOAT32 WAVE file for ElevocSEEngine.
///
/// IMPORTANT for this exact ElevocSEEngine build:
/// Although its WAV validator accepts both 16 kHz and 48 kHz, the subsequent
/// preprocessing routine initializes output_count = input_count / 3 and only
/// fills that output buffer when sampleRate == 48000. A 16 kHz file therefore
/// produces a zero-filled /3 output and fails the 15-second validation.
/// v8 intentionally forces 48 kHz and refuses to fall back to 16 kHz.
/// </summary>
internal sealed class VoiceRegistrationRecorder : IDisposable
{
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const ushort WAVE_FORMAT_PCM = 1;

    private nint _waveIn;
    private nint _buffer;
    private nint _header;
    private int _headerSize;
    private bool _prepared;
    private bool _disposed;

    public async Task<VoiceCaptureResult> RecordAsync(
        string outputWavePath,
        TimeSpan duration,
        IProgress<int>? remainingSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_waveIn != 0)
            throw new InvalidOperationException("录音已经在运行。");

        if (duration < TimeSpan.FromSeconds(16))
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Voice ID 注册录音至少需要 16 秒；测试版固定使用 20 秒。");

        Directory.CreateDirectory(Path.GetDirectoryName(outputWavePath)!);

        const int sampleRate = 48_000;
        uint openResult = Open(sampleRate, duration);

        if (openResult != 0)
        {
            CleanupNative();
            throw new Win32Exception(
                unchecked((int)openResult),
                $"无法以 48 kHz / mono / PCM16 打开默认麦克风，waveInOpen={openResult}。\n" +
                "本版本不会回退到 16 kHz，因为已实机确认该 ElevocSEEngine 的 16 kHz 预处理分支会生成全零 output.pcm。");
        }

        uint result = waveInStart(_waveIn);
        if (result != 0)
        {
            CleanupNative();
            throw new Win32Exception(
                unchecked((int)result),
                $"waveInStart 失败: {result}");
        }

        byte[] pcmBytes;

        try
        {
            int total = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));

            for (int remaining = total; remaining > 0; remaining--)
            {
                remainingSeconds?.Report(remaining);
                await Task.Delay(1000, cancellationToken);
            }

            remainingSeconds?.Report(0);

            _ = waveInStop(_waveIn);
            _ = waveInReset(_waveIn);

            WaveHeader header = Marshal.PtrToStructure<WaveHeader>(_header);
            int bytesRecorded = checked((int)header.dwBytesRecorded);

            if (bytesRecorded <= 0)
                throw new InvalidOperationException("麦克风没有返回任何录音数据。");

            if ((bytesRecorded & 1) != 0)
                bytesRecorded--;

            pcmBytes = new byte[bytesRecorded];
            Marshal.Copy(_buffer, pcmBytes, 0, bytesRecorded);
        }
        finally
        {
            CleanupNative();
        }

        int sampleCount = pcmBytes.Length / 2;
        int minimumSamples = checked(sampleRate * 15);

        if (sampleCount < minimumSamples)
        {
            throw new InvalidOperationException(
                $"有效录音只有 {sampleCount} samples / {sampleRate} Hz = " +
                $"{sampleCount / (double)sampleRate:F2} 秒，低于 Elevoc 的 15 秒门槛。");
        }

        var samples = new float[sampleCount];
        double sumSquares = 0;
        float peak = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * 2;
            short value = unchecked((short)(pcmBytes[offset] | (pcmBytes[offset + 1] << 8)));
            float sample = value / 32768.0f;
            samples[i] = sample;

            float abs = Math.Abs(sample);
            if (abs > peak)
                peak = abs;

            sumSquares += sample * (double)sample;
        }

        double rms = Math.Sqrt(sumSquares / Math.Max(1, sampleCount));

        WriteFloatWave(outputWavePath, samples, sampleRate);

        return new VoiceCaptureResult(
            outputWavePath,
            sampleRate,
            sampleCount,
            sampleCount / (double)sampleRate,
            peak,
            rms);
    }

    private uint Open(int sampleRate, TimeSpan duration)
    {
        var format = new WaveFormatEx
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 1,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = checked((uint)(sampleRate * 2)),
            cbSize = 0
        };

        uint result = waveInOpen(
            out _waveIn,
            WAVE_MAPPER,
            ref format,
            0,
            0,
            0);

        if (result != 0 || _waveIn == 0)
            return result;

        // Add enough headroom that the buffer will never fill before the
        // requested recording interval. waveInReset returns the partially
        // filled buffer and updates dwBytesRecorded.
        int bufferBytes = checked(
            (int)(format.nAvgBytesPerSec *
                  Math.Ceiling(duration.TotalSeconds + 3)));

        _buffer = Marshal.AllocHGlobal(bufferBytes);
        _headerSize = Marshal.SizeOf<WaveHeader>();
        _header = Marshal.AllocHGlobal(_headerSize);

        var header = new WaveHeader
        {
            lpData = _buffer,
            dwBufferLength = (uint)bufferBytes,
            dwBytesRecorded = 0,
            dwUser = 0,
            dwFlags = 0,
            dwLoops = 0,
            lpNext = 0,
            reserved = 0
        };

        Marshal.StructureToPtr(header, _header, false);

        result = waveInPrepareHeader(_waveIn, _header, (uint)_headerSize);
        if (result != 0)
            return result;

        _prepared = true;

        return waveInAddBuffer(_waveIn, _header, (uint)_headerSize);
    }

    private static void WriteFloatWave(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate)
    {
        // WAVE_FORMAT_IEEE_FLOAT = 3, mono, 32-bit.
        const ushort audioFormat = 3;
        const ushort channels = 1;
        const ushort bitsPerSample = 32;
        const ushort blockAlign = 4;

        int dataBytes = checked(samples.Count * sizeof(float));
        uint byteRate = checked((uint)(sampleRate * blockAlign));

        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);

        using var writer = new BinaryWriter(stream);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));

        // RIFF payload:
        // WAVE (4)
        // fmt  chunk (8 + 16)
        // fact chunk (8 + 4)
        // data chunk (8 + dataBytes)
        writer.Write(checked((uint)(48 + dataBytes)));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write(audioFormat);
        writer.Write(channels);
        writer.Write((uint)sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // Non-PCM WAVE convention. Elevoc's parser contains explicit fact
        // handling, so provide a standards-compliant sample-length field.
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fact"));
        writer.Write(4u);
        writer.Write((uint)samples.Count);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);

        foreach (float sample in samples)
            writer.Write(sample);
    }

    private void CleanupNative()
    {
        if (_waveIn != 0)
        {
            if (_prepared && _header != 0)
                _ = waveInUnprepareHeader(_waveIn, _header, (uint)_headerSize);

            _prepared = false;
            _ = waveInClose(_waveIn);
            _waveIn = 0;
        }

        if (_header != 0)
        {
            Marshal.FreeHGlobal(_header);
            _header = 0;
        }

        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }

        _headerSize = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_waveIn != 0)
        {
            _ = waveInStop(_waveIn);
            _ = waveInReset(_waveIn);
        }

        CleanupNative();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nint reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(
        out nint phwi,
        uint uDeviceID,
        ref WaveFormatEx pwfx,
        nint dwCallback,
        nint dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveInPrepareHeader(
        nint hwi,
        nint pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInUnprepareHeader(
        nint hwi,
        nint pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInAddBuffer(
        nint hwi,
        nint pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInStart(nint hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInStop(nint hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInReset(nint hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInClose(nint hwi);
}

internal readonly record struct VoiceCaptureResult(
    string WavePath,
    int SampleRate,
    int SampleCount,
    double DurationSeconds,
    float Peak,
    double Rms);
