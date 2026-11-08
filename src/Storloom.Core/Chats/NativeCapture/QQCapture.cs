// Adapted from chatlog-keeper, Copyright (c) 2026 labazhou2024 (MIT).
// Revision 2225294fd0761e10ce15540f21e7dbb2ac304c3c; see THIRD-PARTY-NOTICES.md.
// Logging removed; verify the selected archive and loaded module before accepting a key.
#nullable disable
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using System.ComponentModel;

namespace Storloom.Core.Chats.NativeCapture.QQ
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // DEBUG_EVENT structure for x64
    // Layout: dwDebugEventCode (4) + dwProcessId (4) + dwThreadId (4) + padding (4) + union (160)
    // Total: 176 bytes
    // We use a byte array for the union and manually parse it to avoid CLR alignment issues
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEBUG_EVENT
    {
        public uint dwDebugEventCode;
        public uint dwProcessId;
        public uint dwThreadId;
        private uint _padding; // 4 bytes padding for 8-byte alignment of union
        // Union as byte array - we'll parse manually
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
        public byte[] u;
    }

    // Helper class for parsing DEBUG_EVENT union data
    internal static class DebugEventParser
    {
        // EXCEPTION_DEBUG_INFO: ExceptionRecord starts at offset 0
        // EXCEPTION_RECORD layout on x64:
        //   0: ExceptionCode (4)
        //   4: ExceptionFlags (4)
        //   8: ExceptionRecord pointer (8)
        //  16: ExceptionAddress pointer (8)
        //  24: NumberParameters (4)
        //  28: padding (4)
        //  32: ExceptionInformation[15] (120)
        public static uint GetExceptionCode(byte[] u)
        {
            return BitConverter.ToUInt32(u, 0);
        }

        public static ulong GetExceptionAddress(byte[] u)
        {
            return BitConverter.ToUInt64(u, 16);
        }

        // CREATE_PROCESS_DEBUG_INFO / LOAD_DLL_DEBUG_INFO: hFile is at offset 0
        public static IntPtr GetFileHandle(byte[] u)
        {
            return (IntPtr)BitConverter.ToInt64(u, 0);
        }

        // EXIT_PROCESS_DEBUG_INFO: dwExitCode is at offset 0
        public static uint GetExitCode(byte[] u)
        {
            return BitConverter.ToUInt32(u, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    internal struct CONTEXT64
    {
        public ulong P1Home;
        public ulong P2Home;
        public ulong P3Home;
        public ulong P4Home;
        public ulong P5Home;
        public ulong P6Home;
        public uint ContextFlags;
        public uint MxCsr;
        public ushort SegCs;
        public ushort SegDs;
        public ushort SegEs;
        public ushort SegFs;
        public ushort SegGs;
        public ushort SegSs;
        public uint EFlags;
        public ulong Dr0;
        public ulong Dr1;
        public ulong Dr2;
        public ulong Dr3;
        public ulong Dr6;
        public ulong Dr7;
        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rsp;
        public ulong Rbp;
        public ulong Rsi;
        public ulong Rdi;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;
        public ulong Rip;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] FltSave;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public ulong[] VectorRegister;
        public ulong VectorControl;
        public ulong DebugControl;
        public ulong LastBranchToRip;
        public ulong LastBranchFromRip;
        public ulong LastExceptionToRip;
        public ulong LastExceptionFromRip;
    }

    internal static class Native
    {
        public const int DEBUG_ONLY_THIS_PROCESS = 0x00000002;
        public const uint INFINITE = 0xFFFFFFFF;

        public const uint EXCEPTION_DEBUG_EVENT = 1;
        public const uint CREATE_THREAD_DEBUG_EVENT = 2;
        public const uint CREATE_PROCESS_DEBUG_EVENT = 3;
        public const uint EXIT_THREAD_DEBUG_EVENT = 4;
        public const uint EXIT_PROCESS_DEBUG_EVENT = 5;
        public const uint LOAD_DLL_DEBUG_EVENT = 6;
        public const uint UNLOAD_DLL_DEBUG_EVENT = 7;
        public const uint OUTPUT_DEBUG_STRING_EVENT = 8;
        public const uint RIP_EVENT = 9;

        public const uint EXCEPTION_BREAKPOINT = 0x80000003;
        public const uint EXCEPTION_SINGLE_STEP = 0x80000004;

        public const uint DBG_CONTINUE = 0x00010002;
        public const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

        public const uint CONTEXT_AMD64 = 0x00100000;
        public const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x0001;
        public const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x0002;
        public const uint CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER | (CONTEXT_AMD64 | 0x0008);
        public const uint CONTEXT_ALL = CONTEXT_FULL | (CONTEXT_AMD64 | 0x0004) | (CONTEXT_AMD64 | 0x0010);

        public const uint TH32CS_SNAPMODULE = 0x00000008;
        public const uint TH32CS_SNAPMODULE32 = 0x00000010;
        public const uint THREAD_ALL_ACCESS = 0x1FFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(
            string lpApplicationName,
            IntPtr lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, int uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
            UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
            UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT64 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT64 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
    }

    /// <summary>
    /// High-level debugger class that extracts the encryption key from QQ process.
    /// All P/Invoke calls are handled internally to avoid PowerShell marshaling issues.
    /// </summary>
    internal sealed class KeyExtractor
    {
        private readonly string _qqExePath;
        private readonly ulong _functionRva;

        private readonly DateTime _deadlineUtc;

        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private uint _processId;
        private ulong _wrapperBase;
        private ulong _breakpointAddress;
        private byte _originalByte;
        private bool _breakpointActive;
        private Dictionary<uint, ulong> _steppingThreads = new Dictionary<uint, ulong>();

        private readonly string _wrapperPath;
        private readonly byte[] _expectedBytes;
        private readonly Func<byte[], bool> _verify;
        private readonly Action<string, int> _diagnostic;
        private int _candidates;
        public KeyExtractor(string qqExePath, ulong functionRva, string wrapperPath, byte[] expectedBytes, Func<byte[], bool> verify, Action<string, int> diagnostic, int timeoutSeconds)
        {
            _qqExePath = qqExePath;
            _wrapperPath = wrapperPath; _expectedBytes = expectedBytes; _verify = verify;
            _diagnostic = diagnostic;
            _functionRva = functionRva;

            _deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        }

        public string ExtractKey()
        {

            if (!StartDebugProcess())
            {
                throw new Exception("启动 QQ 进程并附加调试器失败。错误: " + Marshal.GetLastWin32Error());
            }
            _diagnostic("qq.capture.started", 0);

            try
            {
                return DebugLoop();
            }
            finally
            {
                Cleanup();
            }
        }

        private bool StartDebugProcess()
        {
            STARTUPINFOW si = new STARTUPINFOW();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFOW));
            PROCESS_INFORMATION pi;

            bool result = Native.CreateProcessW(
                _qqExePath,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                Native.DEBUG_ONLY_THIS_PROCESS,
                IntPtr.Zero,
                null,
                ref si,
                out pi);

            if (result)
            {
                _hProcess = pi.hProcess;
                _hThread = pi.hThread;
                _processId = (uint)pi.dwProcessId;
            }

            return result;
        }

        private ulong GetModuleBaseAddress(string moduleName)
        {
            // Use both flags like the Rust implementation
            IntPtr snapshot = Native.CreateToolhelp32Snapshot(
                Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, _processId);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return 0;

            try
            {
                MODULEENTRY32W entry = new MODULEENTRY32W();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));

                if (Native.Module32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFullPath(entry.szExePath), _wrapperPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return (ulong)entry.modBaseAddr;
                        }
                    } while (Native.Module32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                Native.CloseHandle(snapshot);
            }

            return 0;
        }

        private bool SetBreakpoint()
        {
            _breakpointAddress = _wrapperBase + _functionRva;

            byte[] buffer = new byte[_expectedBytes.Length];
            UIntPtr bytesRead;
            if (!Native.ReadProcessMemory(_hProcess, (IntPtr)_breakpointAddress, buffer, new UIntPtr((uint)buffer.Length), out bytesRead) || bytesRead != new UIntPtr((uint)buffer.Length))
            {

                return false;
            }

            if (!buffer.AsSpan().SequenceEqual(_expectedBytes)) throw new InvalidDataException("The loaded QQ code differs from the inspected signed module.");
            _originalByte = buffer[0];

            byte[] int3 = new byte[] { 0xCC };
            UIntPtr bytesWritten;
            if (!Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, int3, new UIntPtr(1), out bytesWritten) || bytesWritten != new UIntPtr(1))
            {

                return false;
            }

            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, new UIntPtr(1));
            _breakpointActive = true;
            _diagnostic("qq.capture.breakpoint-ready", _candidates);

            return true;
        }

        private void RestoreOriginalByte()
        {
            byte[] orig = new byte[] { _originalByte };
            UIntPtr written;
            if (!Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, orig, new UIntPtr(1), out written) || written != new UIntPtr(1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore the QQ breakpoint instruction.");
            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, new UIntPtr(1));
        }

        private void ReinstallBreakpoint()
        {
            byte[] int3 = new byte[] { 0xCC };
            UIntPtr written;
            if (!Native.WriteProcessMemory(_hProcess, (IntPtr)_breakpointAddress, int3, new UIntPtr(1), out written) || written != new UIntPtr(1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot reinstall the QQ key breakpoint.");
            Native.FlushInstructionCache(_hProcess, (IntPtr)_breakpointAddress, new UIntPtr(1));
            _breakpointActive = true;
        }

        private string DebugLoop()
        {
            DEBUG_EVENT debugEvent;
            bool shouldContinue = true;
            string extractedKey = null;  // Store key here instead of returning immediately

            while (shouldContinue)
            {
                double remainingMs = (_deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    _diagnostic("qq.capture.timeout", _candidates);
                    break;
                }
                uint waitMs = (uint)Math.Max(1, Math.Min(10000, remainingMs));
                if (!Native.WaitForDebugEvent(out debugEvent, waitMs))
                {
                    int error = Marshal.GetLastWin32Error();
                    // Timeout (ERROR_SEM_TIMEOUT = 121) is normal, continue waiting
                    if (error == 121)
                        continue;

                    continue;
                }

                uint continueStatus = Native.DBG_CONTINUE;

                switch (debugEvent.dwDebugEventCode)
                {
                    case Native.CREATE_PROCESS_DEBUG_EVENT:

                        // Close the file handle from CREATE_PROCESS_DEBUG_INFO
                        IntPtr hFile = DebugEventParser.GetFileHandle(debugEvent.u);
                        if (hFile != IntPtr.Zero && hFile != new IntPtr(-1))
                            Native.CloseHandle(hFile);
                        break;

                    case Native.CREATE_THREAD_DEBUG_EVENT:

                        break;

                    case Native.EXIT_THREAD_DEBUG_EVENT:

                        break;

                    case Native.LOAD_DLL_DEBUG_EVENT:
                        // Close the file handle from LOAD_DLL_DEBUG_INFO
                        IntPtr dllFileHandle = DebugEventParser.GetFileHandle(debugEvent.u);
                        if (dllFileHandle != IntPtr.Zero && dllFileHandle != new IntPtr(-1))
                            Native.CloseHandle(dllFileHandle);

                        // Check for wrapper.node if not found yet
                        if (_wrapperBase == 0)
                        {
                            ulong baseAddr = GetModuleBaseAddress("wrapper.node");
                            if (baseAddr != 0)
                            {
                                _wrapperBase = baseAddr;

                                ulong targetAddr = _wrapperBase + _functionRva;

                                if (!SetBreakpoint()) throw new InvalidOperationException("Cannot set the QQ key breakpoint.");
                            }
                        }
                        break;

                    case Native.UNLOAD_DLL_DEBUG_EVENT:

                        break;

                    case Native.OUTPUT_DEBUG_STRING_EVENT:
                        // Just continue, we don't care about debug strings
                        break;

                    case Native.EXCEPTION_DEBUG_EVENT:
                        uint exceptionCode = DebugEventParser.GetExceptionCode(debugEvent.u);
                        ulong exceptionAddress = DebugEventParser.GetExceptionAddress(debugEvent.u);

                        if (exceptionCode == Native.EXCEPTION_BREAKPOINT)
                        {
                            // Check if this is our breakpoint
                            if (_breakpointActive && exceptionAddress == _breakpointAddress)
                            {

                                // Restore original byte first
                                RestoreOriginalByte();

                                string foundKey = HandleBreakpoint(debugEvent.dwThreadId);
                                if (foundKey != null)
                                {
                                    // Store key and request termination
                                    extractedKey = foundKey;
                                    _breakpointActive = false;
                                    Native.TerminateProcess(_hProcess, 0);

                                    // Don't return here - continue processing to receive EXIT_PROCESS_DEBUG_EVENT
                                }
                            }
                            // System breakpoint or other breakpoints - just continue
                        }
                        else if (exceptionCode == Native.EXCEPTION_SINGLE_STEP)
                        {
                            if (_steppingThreads.ContainsKey(debugEvent.dwThreadId))
                            {

                                ClearTrapFlag(debugEvent.dwThreadId);
                                ReinstallBreakpoint();
                                _steppingThreads.Remove(debugEvent.dwThreadId);

                            }
                        }
                        else
                        {
                            // Pass unhandled exceptions to the debuggee
                            continueStatus = Native.DBG_EXCEPTION_NOT_HANDLED;
                        }
                        break;

                    case Native.EXIT_PROCESS_DEBUG_EVENT:
                        uint exitCode = DebugEventParser.GetExitCode(debugEvent.u);

                        shouldContinue = false;
                        break;

                    case Native.RIP_EVENT:

                        break;

                    default:

                        break;
                }

                Native.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
            }

            if (extractedKey != null)
            {

                return extractedKey;
            }

            return null;
        }

        private string HandleBreakpoint(uint threadId)
        {
            IntPtr hThread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
            if (hThread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the QQ key-call thread.");

            try
            {
                CONTEXT64 ctx = new CONTEXT64();
                // 只取 整数+控制 寄存器 (R8=key, Rip), 绝不用 CONTEXT_ALL: VectorRegister 声明为
                // ulong[26]=208B 但真实 M128A[26]=416B, CONTEXT_ALL 读写损坏 FPU/SSE → 友机崩
                // (微信调试器同坑实证修复 2026-06-08)。
                ctx.ContextFlags = Native.CONTEXT_INTEGER | Native.CONTEXT_CONTROL;
                ctx.FltSave = new byte[512];
                ctx.VectorRegister = new ulong[26];

                if (!Native.GetThreadContext(hThread, ref ctx))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the QQ key-call registers.");
                }

                // Decrement RIP to point back to original instruction
                ctx.Rip = ctx.Rip - 1;

                // R8 contains the key pointer
                ulong r8Value = ctx.R8;

                int length = unchecked((int)ctx.R9);
                if (length is not (16 or 32)) { SetSingleStep(hThread, ref ctx, threadId); return null; }
                byte[] candidate = new byte[length];
                try {
                    UIntPtr read;
                    if (Native.ReadProcessMemory(_hProcess, (IntPtr)r8Value, candidate, new UIntPtr((uint)length), out read) &&
                        (ulong)read == (ulong)length && Array.TrueForAll(candidate, value => value >= 32 && value <= 126)) {
                        _diagnostic("qq.capture.candidate", ++_candidates);
                        if (_verify(candidate)) { _diagnostic("qq.capture.verified", _candidates); return Encoding.ASCII.GetString(candidate); }
                        _diagnostic("qq.capture.rejected", _candidates);
                    }
                    SetSingleStep(hThread, ref ctx, threadId); return null;
                } finally { CryptographicOperations.ZeroMemory(candidate); }

            }
            finally
            {
                Native.CloseHandle(hThread);
            }
        }

        private void SetSingleStep(IntPtr hThread, ref CONTEXT64 ctx, uint threadId)
        {
            ctx.EFlags = ctx.EFlags | 0x100;
            ctx.ContextFlags = Native.CONTEXT_CONTROL; // 只写回 Rip/EFlags 控制寄存器 (不碰 FltSave/Vector)
            if (!Native.SetThreadContext(hThread, ref ctx)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the QQ key-call instruction.");
            _steppingThreads[threadId] = _breakpointAddress;
            _breakpointActive = false;
        }

        private void ClearTrapFlag(uint threadId)
        {
            IntPtr hThread = Native.OpenThread(Native.THREAD_ALL_ACCESS, false, threadId);
            if (hThread == IntPtr.Zero) return;

            try
            {
                CONTEXT64 ctx = new CONTEXT64();
                ctx.ContextFlags = Native.CONTEXT_CONTROL; // 仅控制寄存器 (清 trap flag), 不用 CONTEXT_ALL 避免 FPU/SSE 损坏
                ctx.FltSave = new byte[512];
                ctx.VectorRegister = new ulong[26];

                if (Native.GetThreadContext(hThread, ref ctx))
                {
                    ctx.EFlags = ctx.EFlags & ~0x100u;
                    Native.SetThreadContext(hThread, ref ctx);
                }
            }
            finally
            {
                Native.CloseHandle(hThread);
            }
        }

        private void Cleanup()
        {
            if (_hProcess != IntPtr.Zero)
            {
                Native.CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
            if (_hThread != IntPtr.Zero)
            {
                Native.CloseHandle(_hThread);
                _hThread = IntPtr.Zero;
            }
        }
    }
}
