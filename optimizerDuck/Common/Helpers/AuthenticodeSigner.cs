using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace optimizerDuck.Common.Helpers;

/// <summary>Compares the Authenticode signers of two executables.</summary>
public static class AuthenticodeSigner
{
    /// <summary>
    ///     Whether <paramref name="candidate" /> may replace <paramref name="current" />: when the
    ///     current file is unsigned there is no publisher to hold the update to, so it passes;
    ///     otherwise the candidate must carry a valid signature from the same subject.
    /// </summary>
    public static bool SameSigner(string current, string candidate, ILogger? logger = null)
    {
        var expected = SubjectOf(current);
        if (expected is null)
            return true;

        if (!IsTrusted(candidate))
        {
            logger?.LogWarning("{Path} has no valid Authenticode signature", candidate);
            return false;
        }

        return string.Equals(expected, SubjectOf(candidate), StringComparison.Ordinal);
    }

    private static string? SubjectOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the signer of a PE file, not a certificate file
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path)
            );
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTrusted(string path)
    {
        var file = new WinTrustFileInfo(path);
        var data = new WinTrustData(file);
        try
        {
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, action, data) == 0;
        }
        finally
        {
            data.Dispose();
            file.Dispose();
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid action,
        WinTrustData data
    );

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly uint _structSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        private readonly IntPtr _filePath;
        private readonly IntPtr _file = IntPtr.Zero;
        private readonly IntPtr _knownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string path) => _filePath = Marshal.StringToCoTaskMemUni(path);

        public void Dispose() => Marshal.FreeCoTaskMem(_filePath);
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class WinTrustData : IDisposable
    {
        private readonly uint _structSize = (uint)Marshal.SizeOf<WinTrustData>();
        private readonly IntPtr _policyCallbackData = IntPtr.Zero;
        private readonly IntPtr _sipClientData = IntPtr.Zero;
        private readonly uint _uiChoice = 2; // WTD_UI_NONE
        private readonly uint _revocationChecks = 0; // WTD_REVOKE_NONE
        private readonly uint _unionChoice = 1; // WTD_CHOICE_FILE
        private readonly IntPtr _fileInfo;
        private readonly uint _stateAction = 0;
        private readonly IntPtr _stateData = IntPtr.Zero;
        private readonly IntPtr _urlReference = IntPtr.Zero;
        private readonly uint _provFlags = 0x00000080; // WTD_REVOCATION_CHECK_NONE
        private readonly uint _uiContext = 0;
        private readonly IntPtr _signatureSettings = IntPtr.Zero;

        public WinTrustData(WinTrustFileInfo fileInfo)
        {
            _fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, _fileInfo, false);
        }

        public void Dispose() => Marshal.FreeCoTaskMem(_fileInfo);
    }
}
