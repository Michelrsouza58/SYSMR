using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SYSMR.Infrastructure.Persistence;

internal static class WindowsSecretProtection
{
    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A proteção de sessão Firebase requer Windows.");
        var clearBytes = System.Text.Encoding.UTF8.GetBytes(value);
        var input = CreateBlob(clearBytes);
        try
        {
            if (!CryptProtectData(ref input, "SYSMR Firebase refresh token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível proteger o token Firebase do usuário do Windows.");
            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }
        finally
        {
            ClearAndFree(input);
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A proteção de sessão Firebase requer Windows.");
        var encryptedBytes = Convert.FromBase64String(protectedValue);
        var input = CreateBlob(encryptedBytes);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível recuperar a sessão Firebase protegida deste usuário do Windows.");
            try
            {
                var clearBytes = new byte[output.Size];
                Marshal.Copy(output.Data, clearBytes, 0, output.Size);
                try
                {
                    return System.Text.Encoding.UTF8.GetString(clearBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clearBytes);
                }
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }
        finally
        {
            ClearAndFree(input);
            CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void ClearAndFree(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
            return;
        for (var index = 0; index < blob.Size; index++)
            Marshal.WriteByte(blob.Data, index, 0);
        Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
