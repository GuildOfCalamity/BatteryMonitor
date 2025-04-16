using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BatteryMonitor;

public static class CryptoHelper
{
    /// <summary>
    /// Encrypts the given text using AES encryption with a specified key.
    /// </summary>
    /// <param name="ClearText">the clear text to encrypt</param>
    /// <param name="Key">should be 16 characters, if not it will be padded with zeros</param>
    public static async Task<string> EncryptAsync(this string ClearText, string Key)
    {
        if (string.IsNullOrEmpty(ClearText))
            throw new ArgumentNullException(nameof(ClearText), "Parameter cannot be null or empty");
        if (string.IsNullOrEmpty(Key))
            throw new ArgumentNullException(nameof(Key), "Parameter cannot be null or empty");

        Encoding encoding = new UTF8Encoding(false);
        using (Aes AES = Aes.Create()) // Use Aes.Create() instead of new AesCryptoServiceProvider
        {
            AES.KeySize = 128;
            AES.Key = encoding.GetBytes(Key.Length > 16 ? Key.Substring(0, 16) : Key.PadRight(16, '0'));
            AES.Mode = CipherMode.CBC;
            AES.Padding = PaddingMode.PKCS7;
            AES.IV = encoding.GetBytes("pDL8avnMJtgQUpxb");
            using (MemoryStream EncryptStream = new MemoryStream())
            {
                using (ICryptoTransform Encryptor = AES.CreateEncryptor())
                {
                    using (CryptoStream TransformStream = new CryptoStream(EncryptStream, Encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter Writer = new StreamWriter(TransformStream, encoding, 512, true))
                        {
                            await Writer.WriteAsync(ClearText);
                        }
                        TransformStream.FlushFinalBlock();
                        return Convert.ToBase64String(EncryptStream.ToArray());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Decrypts the given text using AES encryption with a specified key.
    /// </summary>
    /// <param name="EncryptedText">the encrypted text to decrypt</param>
    /// <param name="Key">should be 16 characters, if not it will be padded with zeros</param>
    public static async Task<string> DecryptAsync(this string EncryptedText, string Key)
    {
        if (string.IsNullOrEmpty(EncryptedText))
            throw new ArgumentNullException(nameof(EncryptedText), "Parameter cannot be null or empty");
        if (string.IsNullOrEmpty(Key))
            throw new ArgumentNullException(nameof(Key), "Parameter cannot be null or empty");

        Encoding encoding = new UTF8Encoding(false);
        using (Aes AES = Aes.Create()) // Use Aes.Create() instead of new AesCryptoServiceProvider  
        {
            AES.KeySize = 128;
            AES.Key = encoding.GetBytes(Key.Length > 16 ? Key.Substring(0, 16) : Key.PadRight(16, '0'));
            AES.Mode = CipherMode.CBC;
            AES.Padding = PaddingMode.PKCS7;
            AES.IV = encoding.GetBytes("pDL8avnMJtgQUpxb");
            using (MemoryStream EncryptStream = new MemoryStream(Convert.FromBase64String(EncryptedText)))
            {
                using (ICryptoTransform Decryptor = AES.CreateDecryptor())
                {
                    using (CryptoStream TransformStream = new CryptoStream(EncryptStream, Decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader Reader = new StreamReader(TransformStream, encoding, true, 512, true))
                        {
                            return await Reader.ReadToEndAsync();
                        }
                    }
                }
            }
        }
    }
}
