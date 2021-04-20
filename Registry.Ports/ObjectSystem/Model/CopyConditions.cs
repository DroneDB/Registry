using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Registry.Ports.ObjectSystem.Model
{
    /// <summary>
    /// A container class to hold all the Conditions to be checked before copying an object.
    /// </summary>
    public class CopyConditions
    {
        private readonly Dictionary<string, string> _copyConditions = new();
        public long ByteRangeStart { get; private set; } = 0;
        public long ByteRangeEnd { get; private set; } = -1;

        /// <summary>
        /// Clone CopyConditions object
        /// </summary>
        /// <returns>new CopyConditions object</returns>
        public CopyConditions Clone()
        {
            var newcond = new CopyConditions();
            foreach (var item in _copyConditions)
            {
                newcond._copyConditions.Add(item.Key, item.Value);
            }
            newcond.ByteRangeStart = ByteRangeStart;
            newcond.ByteRangeEnd = ByteRangeEnd;
            return newcond;
        }

        /// <summary>
        /// Set modified condition, copy object modified since given time.
        /// </summary>
        /// <param name="date"></param>
        /// <exception cref="ArgumentException">When date is null</exception>
        public void SetModified(DateTime date)
        {
            if (date == default)
            {
                throw new ArgumentException("Date cannot be empty", nameof(date));
            }
            _copyConditions.Add("x-amz-copy-source-if-modified-since", date.ToUniversalTime().ToString("r"));
        }

        /// <summary>
        /// Unset modified condition, copy object modified since given time.
        /// </summary>
        /// <param name="date"></param>
        /// <exception cref="ArgumentException">When date is null</exception>
        public void SetUnmodified(DateTime date)
        {
            if (date == default)
            {
                throw new ArgumentException("Date cannot be empty", nameof(date));
            }
            _copyConditions.Add("x-amz-copy-source-if-unmodified-since", date.ToUniversalTime().ToString("r"));
        }

        /// <summary>
        /// Set matching ETag condition, copy object which matches
        /// the following ETag.
        /// </summary>
        /// <param name="etag"></param>
        /// <exception cref="ArgumentException">When etag is null</exception>
        public void SetMatchETag(string etag)
        {
            if (etag == null)
            {
                throw new ArgumentException("ETag cannot be empty", nameof(etag));
            }
            _copyConditions.Add("x-amz-copy-source-if-match", etag);
        }

        /// <summary>
        /// Set matching ETag none condition, copy object which does not
        /// match the following ETag.
        /// </summary>
        /// <param name="etag"></param>
        /// <exception cref="ArgumentException">When etag is null</exception>
        public void SetMatchETagNone(string etag)
        {
            if (etag == null)
            {
                throw new ArgumentException("ETag cannot be empty", nameof(etag));
            }
            _copyConditions.Add("x-amz-copy-source-if-none-match", etag);
        }

        /// <summary>
        /// Set replace metadata directive which specifies that server side copy needs to replace metadata
        /// on destination with custom metadata provided in the request.
        /// </summary>
        public void SetReplaceMetadataDirective()
        {
            _copyConditions.Add("x-amz-metadata-directive", "REPLACE");
        }

        /// <summary>
        /// Return true if replace metadata directive is specified
        /// </summary>
        /// <returns></returns>
        public bool HasReplaceMetadataDirective()
        {
            foreach (var item in _copyConditions)
            {
                if (item.Key.Equals("x-amz-metadata-directive", StringComparison.OrdinalIgnoreCase) &&
                    item.Value.ToUpper().Equals("REPLACE"))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Set Byte Range condition, copy object which falls within the
        /// start and end byte range specified by user
        /// </summary>
        /// <param name="firstByte"></param>
        /// <param name="lastByte"></param>
        /// <exception cref="ArgumentException">When firstByte is null or lastByte is null</exception>
        public void SetByteRange(long firstByte, long lastByte)
        {
            if ((firstByte < 0) || (lastByte < firstByte))
            {
                throw new ArgumentException("Range start less than zero or range end less than range start");
            }

            ByteRangeStart = firstByte;
            ByteRangeEnd = lastByte;
        }

        /// <summary>
        /// Get range size
        /// </summary>
        /// <returns></returns>
        public long GetByteRange()
        {
            return (ByteRangeStart == -1) ? 0 : (ByteRangeEnd - ByteRangeStart + 1);
        }

        /// <summary>
        /// Get all the set copy conditions map.
        /// </summary>
        /// <returns></returns>
        public ReadOnlyDictionary<string, string> GetConditions()
        {
            return new(_copyConditions);
        }
    }

}
