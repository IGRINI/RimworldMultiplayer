using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Multiplayer.Common
{
    public class ByteWriter
    {
        private MemoryStream stream;
        public object? context;

        public int Position => (int)stream.Position;

        public ByteWriter(int capacity = 0)
        {
            stream = new MemoryStream(capacity);
        }

        public virtual void WriteByte(byte val) => stream.WriteByte(val);

        public virtual void WriteSByte(sbyte val) => stream.WriteByte((byte)val);

        public virtual void WriteShort(short val)
        {
            Span<byte> buf = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteUShort(ushort val)
        {
            Span<byte> buf = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteInt32(int val)
        {
            Span<byte> buf = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteUInt32(uint val)
        {
            Span<byte> buf = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteLong(long val)
        {
            Span<byte> buf = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteULong(ulong val)
        {
            Span<byte> buf = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, val);
            WriteSpan(buf);
        }

        public virtual void WriteFloat(float val)
        {
            Span<byte> buf = stackalloc byte[sizeof(float)];
            // Reinterpret IEEE-754 bit pattern as int32 and write LE. Same wire bytes as
            // BitConverter.GetBytes(float) on LE machines.
#if NET48
            int bits = new FloatIntUnion(val).Int;
#else
            int bits = BitConverter.SingleToInt32Bits(val);
#endif
            BinaryPrimitives.WriteInt32LittleEndian(buf, bits);
            WriteSpan(buf);
        }

        public virtual void WriteDouble(double val)
        {
            Span<byte> buf = stackalloc byte[sizeof(double)];
            long bits = BitConverter.DoubleToInt64Bits(val);
            BinaryPrimitives.WriteInt64LittleEndian(buf, bits);
            WriteSpan(buf);
        }

        public virtual void WriteBool(bool val) => stream.WriteByte(val ? (byte)1 : (byte)0);

        // MemoryStream lacks Span overloads on netstandard2.0 / net48, so we route through a
        // small per-instance scratch buffer to avoid allocating per call.
        [ThreadStatic] private static byte[]? scratch;

        private void WriteSpan(ReadOnlySpan<byte> data)
        {
            int len = data.Length;
            var buf = scratch ??= new byte[8];
            data.CopyTo(buf);
            stream.Write(buf, 0, len);
        }

#if NET48
        // Equivalent of BitConverter.SingleToInt32Bits for net48 (no unsafe needed).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private readonly struct FloatIntUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public readonly float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] public readonly int Int;
            public FloatIntUnion(float f) : this() { Float = f; }
        }
#endif

        public virtual void WritePrefixedBytes(byte[]? bytes)
        {
            if (bytes == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(bytes.Length);
            WriteRaw(bytes);
        }

        public virtual void WritePrefixedInts(IList<int> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteInt32(@int);
        }

        public virtual void WritePrefixedUInts(IList<uint> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteUInt32(@int);
        }

        public virtual void WriteRaw(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        public virtual void WriteFrom(byte[] buffer, int offset, int length)
        {
            stream.Write(buffer, offset, length);
        }

        public virtual ByteWriter WriteString(string? s)
        {
            WritePrefixedBytes(s == null ? null : Encoding.UTF8.GetBytes(s));
            return this;
        }

        public virtual void WriteEnum<T>(T value) where T : Enum
        {
            Type type = Enum.GetUnderlyingType(typeof(T) == typeof(Enum) ? value.GetType() : typeof(T));

            if (type == typeof(byte))
            {
                WriteByte(Convert.ToByte(value));
            }
            else if (type == typeof(sbyte))
            {
                WriteSByte(Convert.ToSByte(value));
            }
            else if (type == typeof(short))
            {
                WriteShort(Convert.ToInt16(value));
            }
            else if (type == typeof(ushort))
            {
                WriteUShort(Convert.ToUInt16(value));
            }
            else if (type == typeof(int))
            {
                WriteInt32(Convert.ToInt32(value));
            }
            else if (type == typeof(uint))
            {
                WriteUInt32(Convert.ToUInt32(value));
            }
            else if (type == typeof(long) || type == typeof(IntPtr))
            {
                WriteLong(Convert.ToInt64(value));
            }
            else if (type == typeof(ulong) || type == typeof(UIntPtr))
            {
                WriteULong(Convert.ToUInt64(value));
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.WriteEnum: Unknown type {type}");
            }
        }
        private void Write(object obj)
        {
            if (obj is int @int)
            {
                WriteInt32(@int);
            }
            else if (obj is ushort @ushort)
            {
                WriteUShort(@ushort);
            }
            else if (obj is short @short)
            {
                WriteShort(@short);
            }
            else if (obj is bool @bool)
            {
                WriteBool(@bool);
            }
            else if (obj is long @long)
            {
                WriteLong(@long);
            }
            else if (obj is ulong @ulong)
            {
                WriteULong(@ulong);
            }
            else if (obj is byte @byte)
            {
                WriteByte(@byte);
            }
            else if (obj is float @float)
            {
                WriteFloat(@float);
            }
            else if (obj is double @double)
            {
                WriteDouble(@double);
            }
            else if (obj is byte[] bytes)
            {
                WritePrefixedBytes(bytes);
            }
            else if (obj is Enum enumObj)
            {
                WriteEnum(enumObj);
            }
            else if (obj is string @string)
            {
                WriteString(@string);
            }
            else if (obj is Array arr)
            {
                Write(arr.Length);
                foreach (object o in arr)
                    Write(o);
            }
            else if (obj is IList list)
            {
                Write(list.Count);
                foreach (object o in list)
                    Write(o);
            }
            else if (obj is ITuple tuple)
            {
                for (int i = 0; i < tuple.Length; i++)
                    Write(tuple[i]);
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.Write: Unknown type {obj.GetType()}");
            }
        }

        public byte[] ToArray()
        {
            return stream.ToArray();
        }

        /// <summary>
        /// Writes all objects in the order given and returns the resulting bytes.
        /// </summary>
        public static byte[] GetBytes(params object[] data)
        {
            var writer = new ByteWriter();
            foreach (object o in data)
                writer.Write(o);
            return writer.ToArray();
        }

        public void SetLength(long value)
        {
            stream.SetLength(value);
        }
    }

    public class WriterException : Exception
    {
        public WriterException(string msg) : base(msg)
        {
        }
    }

}
