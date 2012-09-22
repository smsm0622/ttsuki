
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections;
using System.Windows.Forms;
using Tsukikage.Windows.Messaging;

namespace Tsukikage.WinMM.WaveIO
{
    /// <summary>
    /// Win32 WaveOut ����N���X
    /// </summary>
    [System.Security.SuppressUnmanagedCodeSecurity]
    public class WaveOut : IDisposable
    {
        public delegate void WaveOutDoneHandler();

        IntPtr deviceHandle = IntPtr.Zero;
        int enqueuedBufferSize = 0;
        MessageThread eventHandler;

        public const int WaveMapper = -1;
        public IntPtr Handle { get { return deviceHandle; } }
        
        /// <summary>
        /// Not played yet (contains playing data).
        /// �Đ����I����ĂȂ��f�[�^�̗�(Write�P��)
        /// </summary>
        public int EnqueuedBufferSize { get { return enqueuedBufferSize; } }

        /// <summary>
        /// On complete played one buffer.
        /// Write����Wave�f�[�^�̍Đ����I���ƌĂяo����܂��B
        /// </summary>
        /// <remarks>
        /// The event will be called from another thread.
        /// �C�x���g�͕ʂ̃X���b�h����Ă΂�邱�Ƃ�����܂��B
        /// </remarks>
        public event WaveOutDoneHandler OnDone;

        /// <summary>
        /// Open WaveOut.
        /// WaveOut���J���܂��B
        /// </summary>
        /// <param name="deviceId">WaveOut.WaveMapper���AGetDeviceNames()��index</param>
        /// <param name="samplesPerSec">44100 (Hz)</param>
        /// <param name="bitsPerSample">16 or 8</param>
        /// <param name="channels">2 or 1</param>
        public WaveOut(int deviceId, int samplesPerSec, int bitsPerSample, int channels)
        {
            Win32.WaveFormatEx wfx = new Win32.WaveFormatEx(samplesPerSec, bitsPerSample, channels);

            eventHandler = new MessageThread();
            IntPtr ptr = new IntPtr(eventHandler.Win32ThreadID);

            int mmret = Win32.waveOutOpen(out deviceHandle, (uint)deviceId, ref wfx, ptr, IntPtr.Zero, Win32.CALLBACK_THREAD);
            if (mmret != Win32.MMSYSERR_NOERROR)
            {
                eventHandler.Dispose();
                throw new Exception("�f�o�C�X���J���܂���ł����B(" + mmret + ")");
            }

            eventHandler.MessageHandlers[Win32.MM_WOM_DONE] = delegate(Message m)
            {
                Win32.WaveHeader header = Win32.WaveHeader.FromIntPtr(m.LParam);
                WaveBuffer buf = WaveBuffer.FromWaveHeader(header);
                Win32.waveOutUnprepareHeader(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
                Interlocked.Add(ref enqueuedBufferSize, -buf.Data.Length);
                buf.Dispose();
                if (OnDone != null)
                    OnDone();
            };
        }

        void EnsureOpened()
        {
            if (deviceHandle == IntPtr.Zero)
                throw new InvalidOperationException("�J���ĂȂ��񂾂��ǁI");
        }

        /// <summary>
        /// Write data to WaveOut.
        /// �����o��
        /// </summary>
        /// <param name="waveform">��</param>
        public void Write(byte[] waveform)
        {
            EnsureOpened();
            WaveBuffer buf = new WaveBuffer((uint)waveform.Length);
            Array.Copy(waveform, buf.Data, waveform.Length);
            Interlocked.Add(ref enqueuedBufferSize, waveform.Length);
            Win32.waveOutPrepareHeader(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
            Win32.waveOutWrite(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
        }

        /// <summary>
        /// Stop.
        /// �~�߂�B
        /// </summary>
        public void Stop()
        {
            EnsureOpened();
            Win32.waveOutReset(deviceHandle);
            while (enqueuedBufferSize != 0)
                Thread.Sleep(0);
        }

        /// <summary>
        /// Close WaveOut and release all resources.
        /// WaveOut����A���ׂẴ��\�[�X��������܂��B
        /// </summary>
        public void Close()
        {
            if (deviceHandle != IntPtr.Zero)
            {
                Stop();
                Win32.waveOutClose(deviceHandle);
                deviceHandle = IntPtr.Zero;
                eventHandler.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        void IDisposable.Dispose()
        {
            Close();
        }

        /// <summary>
        /// Get names of installed devices.
        /// �C���X�g�[���ς݂̃f�o�C�X���𓾂܂��B
        /// </summary>
        /// <returns></returns>
        public static string[] GetDeviceNames()
        {
            uint devs = Win32.waveOutGetNumDevs();
            string[] devNames = new string[devs];
            for (uint i = 0; i < devs; i++)
            {
                Win32.WaveOutCaps caps = new Win32.WaveOutCaps();
                Win32.waveOutGetDevCaps(i, out caps, Win32.WaveOutCaps.SizeOfWaveOutCaps);
                devNames[i] = caps.szPname;
            }
            return devNames;
        }
    }

    /// <summary>
    /// Win32 WaveIn����N���X
    /// </summary>
    [System.Security.SuppressUnmanagedCodeSecurity]
    public class WaveIn : IDisposable
    {
        public delegate void WaveInDataHandler(byte[] data);

        IntPtr deviceHandle = IntPtr.Zero;
        MessageThread messageProc;
        public const int WaveMapper = -1;

        public IntPtr Handle { get { return deviceHandle; } }

        /// <summary>
        /// On data arrival.
        /// �f�[�^������Ƃ��ɌĂяo����܂��B
        /// </summary>
        /// <remarks>
        /// The event will be called from another thread.
        /// �C�x���g�͕ʂ̃X���b�h����Ă΂�邱�Ƃ�����܂��B
        /// </remarks>
        public event WaveInDataHandler OnData;
       
        int enqueuedBufferCount = 0;
        volatile bool recording = false;

        /// <summary>
        /// Open WaveIn.
        /// WaveIn���J���܂��B
        /// </summary>
        /// <param name="deviceId">WaveIn.WaveMapper or index of GetDeviceNames(). WaveIn.WaveMapper���AGetDeviceNames()��index</param>
        /// <param name="samplesPerSec">ex) 44100 (Hz)</param>
        /// <param name="bitsPerSample">16 or 8</param>
        /// <param name="channels">2 or 1</param>
        public WaveIn(int deviceId, int samplesPerSec, int bitsPerSample, int channels)
        {
            Win32.WaveFormatEx wfx = new Win32.WaveFormatEx(samplesPerSec, bitsPerSample, channels);

            messageProc = new MessageThread();
            messageProc.MessageHandlers[Win32.MM_WIM_DATA] = delegate(Message m)
            {
                Win32.WaveHeader header = Win32.WaveHeader.FromIntPtr(m.LParam);
                WaveBuffer buf = WaveBuffer.FromWaveHeader(header);

                int bytesRecorded = (int)header.dwBytesRecorded;
                if (OnData != null && bytesRecorded != 0)
                {
                    byte[] data = new byte[bytesRecorded];
                    Array.Copy(buf.Data, data, bytesRecorded);
                    OnData(data);
                }

                if (recording)
                {
                    Win32.waveInAddBuffer(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
                }
                else
                {
                    Win32.waveInUnprepareHeader(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
                    buf.Dispose();
                    Interlocked.Decrement(ref enqueuedBufferCount);
                }
            };

            int mmret = Win32.waveInOpen(out deviceHandle, (uint)deviceId, ref wfx, new IntPtr(messageProc.Win32ThreadID), IntPtr.Zero, Win32.CALLBACK_THREAD);
            if (mmret != Win32.MMSYSERR_NOERROR)
            {
                messageProc.Dispose();
                throw new Exception("�f�o�C�X���J���܂���ł����B(" + mmret + ")");
            }
        }

        void EnsureOpened()
        {
            if (deviceHandle == IntPtr.Zero)
                throw new InvalidOperationException("�J���ĂȂ��񂾂��ǁI");
        }

        /// <summary>
        /// Start recording. �^���J�n
        /// </summary>
        public void Start() { Start(65536, 16); }

        /// <summary>
        /// Start recording. �^���J�n
        /// </summary>
        /// <param name="bufferSize"> ex) 65536 : �o�b�t�@�T�C�Y</param>
        public void Start(int bufferSize) { Start(16, bufferSize); }

        /// <summary>
        /// Start recording. �^���J�n
        /// </summary>
        /// <param name="bufferSize"> ex) 16 : �o�b�t�@��</param>
        /// <param name="bufferSize"> ex) 65536 : �o�b�t�@�T�C�Y</param>
        public void Start(int bufferCount, int bufferSize)
        {
            EnsureOpened();
            if (recording)
                throw new InvalidOperationException("���ɘ^����");

            for (int i = 0; i < bufferCount; i++)
            {
                WaveBuffer buf = new WaveBuffer((uint)bufferSize);
                Win32.waveInPrepareHeader(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
                Win32.waveInAddBuffer(deviceHandle, buf.pHeader, Win32.WaveHeader.SizeOfWaveHeader);
                Interlocked.Add(ref enqueuedBufferCount, 1);
            }

            int mmret = Win32.waveInStart(deviceHandle);
            if (mmret != Win32.MMSYSERR_NOERROR)
            {
                throw new Exception("�^���J�n�Ɏ��s�c�c�H (" + mmret + ")");
            }
            recording = true;
        }

        /// <summary>
        /// Stop recording. �^����~
        /// </summary>
        public void Stop()
        {
            EnsureOpened();
            recording = false;
            Win32.waveInReset(deviceHandle);
            while (enqueuedBufferCount != 0)
                Thread.Sleep(0);
        }

        /// <summary>
        /// Close WaveIn and release all resources.
        /// WaveIn����A���ׂẴ��\�[�X��������܂��B
        /// </summary>
        public void Close()
        {
            if (deviceHandle != IntPtr.Zero)
            {
                OnData = null;
                Stop();
                Win32.waveInClose(deviceHandle);
                deviceHandle = IntPtr.Zero;
                messageProc.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        void IDisposable.Dispose()
        {
            Close();
        }

        /// <summary>
        /// Get names of installed devices.
        /// �C���X�g�[���ς݂̃f�o�C�X���𓾂܂��B
        /// </summary>
        /// <returns></returns>
        public static string[] GetDeviceNames()
        {
            uint devs = Win32.waveInGetNumDevs();
            string[] devNames = new string[devs];
            for (uint i = 0; i < devs; i++)
            {
                Win32.WaveInCaps caps = new Win32.WaveInCaps();
                Win32.waveInGetDevCaps(i, out caps, Win32.WaveInCaps.SizeOfWaveInCaps);
                devNames[i] = caps.szPname;
            }
            return devNames;
        }
    }

    [System.Security.SuppressUnmanagedCodeSecurity]
    class WaveBuffer : IDisposable
    {
        public IntPtr pHeader { get; private set; }
        public byte[] Data { get; private set; }
        GCHandle dataHandle;
        GCHandle bufferHandle;

        public WaveBuffer(uint dwSize)
        {
            Data = new byte[dwSize];
            dataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            bufferHandle = GCHandle.Alloc(this);

            Win32.WaveHeader header = new Win32.WaveHeader();
            header.lpData = dataHandle.AddrOfPinnedObject();
            header.dwBufferLength = (uint)Data.Length;
            header.dwUser = GCHandle.ToIntPtr(bufferHandle);

            pHeader = Marshal.AllocHGlobal(Win32.WaveHeader.SizeOfWaveHeader);
            Marshal.StructureToPtr(header, pHeader, true);
        }

        public static WaveBuffer FromWaveHeader(Win32.WaveHeader header)
        {
            return (WaveBuffer)GCHandle.FromIntPtr(header.dwUser).Target;
        }

        public void Dispose()
        {
            if (pHeader == IntPtr.Zero)
                return;

            bufferHandle.Free();
            dataHandle.Free();
            Marshal.FreeHGlobal(pHeader);
            pHeader = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }
    }

    [System.Security.SuppressUnmanagedCodeSecurity]
    public static class Win32
    {
        public const int MMSYSERR_NOERROR = 0;
        public const int CALLBACK_WINDOW = 0x00010000;
        public const int CALLBACK_THREAD = 0x00020000;
        public const int CALLBACK_FUNCTION = 0x00030000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WaveFormatEx
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;

            public WaveFormatEx(int SamplesPerSec, int BitsPerSample, int Channels)
            {
                wFormatTag = (short)WAVE_FORMAT_PCM;
                nSamplesPerSec = SamplesPerSec;
                nChannels = (short)Channels;
                wBitsPerSample = (short)BitsPerSample;
                nBlockAlign = (short)(Channels * BitsPerSample >> 3);
                nAvgBytesPerSec = SamplesPerSec * nBlockAlign;
                cbSize = 0;
            }

            public static int WAVE_FORMAT_PCM { get { return 1; } }
            public static int SizeOfWaveFormatEx { get { return Marshal.SizeOf(typeof(WaveFormatEx)); } }
        }

        public const int MM_WOM_DONE = 0x3BD;
        public const int MM_WIM_DATA = 0x3C0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct WaveHeader
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
            public static int SizeOfWaveHeader { get { return Marshal.SizeOf(typeof(WaveHeader)); } }
            public static WaveHeader FromIntPtr(IntPtr p) { return (WaveHeader)Marshal.PtrToStructure(p, typeof(WaveHeader)); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WaveOutCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
            public static int SizeOfWaveOutCaps { get { return Marshal.SizeOf(typeof(WaveOutCaps)); } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WaveInCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public static int SizeOfWaveInCaps { get { return Marshal.SizeOf(typeof(WaveInCaps)); } }
        }

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutGetDevCaps(uint uDeviceID, out WaveOutCaps pwoc, int cbwoc);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwCallbackInstance, int fdwOpen);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutPrepareHeader(IntPtr hwo, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutWrite(IntPtr hwo, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveOutClose(IntPtr hwo);


        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInGetDevCaps(uint uDeviceID, out WaveInCaps pwic, int cbwic);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwCallbackInstance, int fdwOpen);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInPrepareHeader(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInAddBuffer(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInUnprepareHeader(IntPtr hwi, ref WaveHeader pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        public static extern int waveInClose(IntPtr hwi);
    }
}