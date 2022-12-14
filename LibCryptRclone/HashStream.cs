using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LibCryptRclone
{
    public interface IHashStream
    {
        string Hash
        {
            get;
        }
    }


    class HashStream : Stream, IHashStream
    {
        Stream innerStream;
        HashAlgorithm hasher;
        bool Invalid = false;
        long pos;
        bool lengthSeek = false;

        bool disposed;

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            if (!disposed)
            {
                if (isDisposing)
                {
                    innerStream?.Dispose();
                }
                disposed = true;
            }
        }

        public string Hash
        {
            get
            {
                Flush();
                return (Invalid) ? "" : BitConverter.ToString(hasher.Hash).Replace("-", "");
            }
        }

        public HashStream(Stream s, HashAlgorithm hash) : base()
        {
            innerStream = s;
            try
            {
                pos = innerStream.Position;
            }
            catch
            {
                pos = 0;
            }
            hasher = hash;
        }

        public HashStream(HashAlgorithm hash) : base()
        {
            pos = 0;
            hasher = hash;
        }

        public override long Length
        {
            get
            {
                if (innerStream == null) throw new NotImplementedException();
                return innerStream.Length;
            }
        }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override void Flush()
        {
            hasher.TransformFinalBlock(new byte[0], 0, 0);
        }

        public override long Position
        {
            get
            {
                return pos;
            }
            set
            {
                if (pos != value) Invalid = true;
                if (innerStream != null) innerStream.Position = value;
                else throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (innerStream == null) return -1;
            int ret = innerStream.Read(buffer, offset, count);
            if (!lengthSeek && !Invalid)
                hasher.TransformBlock(buffer, offset, ret, buffer, offset);
            pos += ret;
            return ret;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (offset == 0 && origin == SeekOrigin.Current) return pos;
            if (pos == 0 && offset == 0 && origin == SeekOrigin.Begin)
            {
                lengthSeek = false;
            }
            else if (pos == 0 && offset == 0 && origin == SeekOrigin.End)
            {
                lengthSeek = true;
            }
            else
            {
                Invalid = true;
            }
            if (innerStream != null)
                return innerStream.Seek(offset, origin);
            else
                throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (lengthSeek || Invalid) return;
            hasher.TransformBlock(buffer, offset, count, buffer, offset);
            pos += count;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }
    }
}
