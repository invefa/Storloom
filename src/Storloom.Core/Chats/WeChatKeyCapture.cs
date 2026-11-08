using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Storloom.Core.Chats.NativeCapture;

namespace Storloom.Core.Chats;

public static class WeChatKeyCapture {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class TrustFile {
        public uint Size = (uint)Marshal.SizeOf<TrustFile>();
        [MarshalAs(UnmanagedType.LPWStr)] public string Path = "";
        public nint Handle;
        public nint Subject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private sealed class TrustData {
        public uint Size = (uint)Marshal.SizeOf<TrustData>();
        public nint Policy, Sip;
        public uint Ui = 2, Revocation, Choice = 1;
        public nint File;
        public uint StateAction;
        public nint State, Url;
        public uint Flags = 0x1010, Context;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, TrustData data);

    public static void ValidateTencentSignature(string path) {
        PathPolicy.AssertNoLinks(path);
        var file = new TrustFile { Path = path };
        nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try {
            Marshal.StructureToPtr(file, memory, false);
            var data = new TrustData { File = memory };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(0, ref action, data) != 0)
                throw new CryptographicException("The selected client has no valid, locally trusted Authenticode signature.");
            // This API extracts the PE signer; WinVerifyTrust above validates the binary signature.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!cert.Subject.Contains("Tencent", StringComparison.OrdinalIgnoreCase) &&
                !cert.Subject.Contains("腾讯", StringComparison.Ordinal))
                throw new CryptographicException("The client is not signed by Tencent.");
        } finally {
            Marshal.DestroyStructure<TrustFile>(memory);
            Marshal.FreeHGlobal(memory);
        }
    }

    public static byte[] Capture(ChatLocation location, string executable,
        CancellationToken cancellation = default, IProgress<ChatDiagnostic>? diagnostics = null) {
        if (location.Platform != ChatPlatform.WeChat) throw new InvalidOperationException("Active capture supports WeChat 4.x only.");
        if (CleanupEngine.RunningProcesses(new CleanupRule { Processes = ["Weixin", "WeChat"] }).Length > 0)
            throw new IOException("Close WeChat before starting its separate key-capture instance.");
        executable = PathPolicy.Normalize(executable);
        var clock = Stopwatch.StartNew();
        diagnostics?.Report(new("wechat.capture.inspect"));
        if (!Path.GetFileName(executable).Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the installed Weixin.exe for a supported WeChat 4.x client.");
        ValidateTencentSignature(executable);
        string directory = Path.GetDirectoryName(executable)!;
        string[] libraries = Directory.GetFiles(directory, "Weixin.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(directory).Select(p => Path.Combine(p, "Weixin.dll"))
                .Where(File.Exists)).ToArray();
        if (libraries.Length != 1)
            throw new InvalidDataException("Select an unambiguous client version containing exactly one Weixin.dll.");
        string library = libraries[0];
        ValidateTencentSignature(library);
        byte[] image = File.ReadAllBytes(library);
        ulong[] rvas = Locator.FindKeySetFunctionRvas(image, _ => { });
        if (rvas.Length == 0) throw new InvalidDataException("Unsupported client signature.");
        diagnostics?.Report(new("wechat.capture.signature-and-function-verified", ElapsedSeconds: clock.Elapsed.TotalSeconds));
        byte[] firstPage = new byte[ChatCipher.PageSize];
        using (var input = new FileStream(location.Database, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete)) input.ReadExactly(firstPage);
        cancellation.ThrowIfCancellationRequested();
        var extractor = new KeyExtractor(executable, rvas, firstPage,
            marker => { if (marker == "CHATLOG_KEY_RECOVERY_CLIENT_OPEN_V1") diagnostics?.Report(new("wechat.capture.started", Processes: 1, ElapsedSeconds: clock.Elapsed.TotalSeconds)); }, _ => { }, 90);
        string found = extractor.ExtractKey();
        byte[] key = string.IsNullOrWhiteSpace(found) ? [] : Convert.FromHexString(found);
        if (cancellation.IsCancellationRequested) {
            CryptographicOperations.ZeroMemory(key);
            cancellation.ThrowIfCancellationRequested();
        }
        if (key.Length != 32 || ChatCipher.Detect(firstPage, key, ChatPlatform.WeChat) == null) {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The captured value did not authenticate the selected account database.");
        }
        diagnostics?.Report(new("wechat.capture.verified", Processes: 1, ElapsedSeconds: clock.Elapsed.TotalSeconds)); return key;
    }
}
