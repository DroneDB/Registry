using System;
using System.Collections.Generic;
using System.Text;
using Minio.DataModel;
using Registry.Ports.ObjectSystem.Model;
using SSEC = Minio.DataModel.SSEC;

namespace Registry.Adapters
{
    public static class AdaptersUtils
    {
        public static ServerSideEncryption ToSSE(this IServerEncryption encryption)
        {
            if (encryption is EncryptionC ssec)
            {
                return new SSEC(ssec.Key);
            }

            if (encryption is EncryptionKMS ssekms)
            {
                return new SSEKMS(ssekms.Key, ssekms.Context);
            }

            if (encryption is EncryptionS3 sses3)
            {
                return new SSES3();
            }

            if (encryption is EncryptionCopy ssecopy)
            {
                return new SSECopy(ssecopy.Key);
            }

            throw new ArgumentException($"Encryption not supported: '{encryption.GetType().Name}'");

        }
    }
}
