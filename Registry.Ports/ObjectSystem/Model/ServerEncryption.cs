using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Ports.ObjectSystem.Model
{

    public interface IServerEncryption
    {
        
    }

    /// <summary>
    /// Server-side encryption with AWS KMS managed keys
    /// </summary>
    public class EncryptionKMS : IServerEncryption
    {
        public string Key { get; }
        public Dictionary<string, string> Context { get; private set; }

        public EncryptionKMS(string key, Dictionary<string, string> context = null)
        {
            Key = key;
            Context = context;
        }
        
    }

    /// <summary>
    /// Server-side encryption with S3 managed encryption keys (SSE-S3)
    /// </summary>
    public class EncryptionS3 : IServerEncryption
    {
        
    }

    /// <summary>
    /// Server-side encryption with customer provided keys (SSE-C)
    /// </summary>
    public class EncryptionC : IServerEncryption
    {
        // secret AES-256 Key
        public byte[] Key { get; }

        public EncryptionC(byte[] key)
        {
            if (key == null || key.Length != 32)
            {
                throw new ArgumentException("Secret key needs to be a 256 bit AES Key", nameof(key));
            }
            this.Key = key;
        }
    }

    /// <summary>
    /// Server-side encryption option for source side SSE-C copy operation
    /// </summary>
    public class EncryptionCopy : IServerEncryption
    {

        // secret AES-256 Key
        public byte[] Key { get; }

        public EncryptionCopy(byte[] key)
        {
            if (key == null || key.Length != 32)
            {
                throw new ArgumentException("Secret key needs to be a 256 bit AES Key", nameof(key));
            }
            this.Key = key;
        }

    }


}
