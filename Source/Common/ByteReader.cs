using System;
using System.Buffers.Binary;
using System.Text;

namespace Multiplayer.Common
{
    public class ByteReader
    {
        const int DefaultMaxStringLen = 32767;

        private readonly byte[] array;
        public object? context;

        public int Length => array.Length;
        public int Position { get; private set; }
        public int Left => Length - Position;

        public ByteReader(byte[] array)
        {
            this.array = array;
        }

        public virtual byte PeekByte() => array[Position];

        public virtual byte ReadByte() => array[IncrementIndex(1)];

        public virtual sbyte ReadSByte() => (sbyte)array[IncrementIndex(1)];

        public virtual short ReadShort() =>
            BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(2), 2));

        public virtual ushort ReadUShort() =>
            BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(2), 2));

        public virtual int ReadInt32() =>
            BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(4), 4));

        public virtual uint ReadUInt32() =>
            BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(4), 4));

        public virtual long ReadLong() =>
            BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(8), 8));

        public virtual ulong ReadULong() =>
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(8), 8));

        public virtual float ReadFloat()
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(4), 4));
#if NET48
            return new IntFloatUnion(bits).Float;
#else
            return BitConverter.Int32BitsToSingle(bits);
#endif
        }

        public virtual double ReadDouble()
        {
            long bits = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(array, IncrementIndex(8), 8));
            return BitConverter.Int64BitsToDouble(bits);
        }

        public virtual bool ReadBool() => array[IncrementIndex(1)] != 0;

#if NET48
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private readonly struct IntFloatUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public readonly int Int;
            [System.Runtime.InteropServices.FieldOffset(0)] public readonly float Float;
            public IntFloatUnion(int i) : this() { Int = i; }
        }
#endif

        public virtual string? ReadStringNullable(int maxLen = DefaultMaxStringLen)
        {
            int bytes = ReadInt32();
            if (bytes == -1) return null;

            if (bytes < -1)
                throw new ReaderException($"String byte length ({bytes}<-1)");
            if (bytes > maxLen)
                throw new ReaderException($"String too long ({bytes}>{maxLen})");

            string result = Encoding.UTF8.GetString(array, Position, bytes);
            Position += bytes;

            return result;
        }

        public virtual string ReadString(int maxLen = DefaultMaxStringLen)
        {
            int bytes = ReadInt32();

            if (bytes < 0)
                throw new ReaderException($"String byte length ({bytes}<0)");
            if (bytes > maxLen)
                throw new ReaderException($"String too long ({bytes}>{maxLen})");

            string result = Encoding.UTF8.GetString(array, Position, bytes);
            Position += bytes;

            return result;
        }

        public virtual byte[] ReadRaw(int len)
        {
            return array.SubArray(IncrementIndex(len), len);
        }

        public virtual byte[]? ReadPrefixedBytes(int maxLen = int.MaxValue)
        {
            int len = ReadInt32();
            if (len == -1) return null;

            if (len < -1)
                throw new ReaderException($"Byte array length ({len}<-1)");
            if (len >= maxLen)
                throw new ReaderException($"Byte array too long ({len}>{maxLen})");

            return ReadRaw(len);
        }

        public virtual int[] ReadPrefixedInts(int maxLen = int.MaxValue)
        {
            int len = ReadInt32();

            if (len < 0)
                throw new ReaderException($"Int array length ({len}<0)");
            if (len > maxLen)
                throw new ReaderException($"Int array too long ({len}>{maxLen})");

            int[] result = new int[len];
            for (int i = 0; i < len; i++)
                result[i] = ReadInt32();

            return result;
        }

        public virtual uint[] ReadPrefixedUInts()
        {
            int len = ReadInt32();
            uint[] result = new uint[len];
            for (int i = 0; i < len; i++)
                result[i] = ReadUInt32();
            return result;
        }

        public virtual ulong[] ReadPrefixedULongs()
        {
            int len = ReadInt32();
            ulong[] result = new ulong[len];
            for (int i = 0; i < len; i++) {
                result[i] = ReadULong();
            }
            return result;
        }

        public virtual string?[] ReadPrefixedStrings()
        {
            int len = ReadInt32();
            string?[] result = new string[len];
            for (int i = 0; i < len; i++)
                result[i] = ReadString();
            return result;
        }

        public virtual T ReadEnum<T>() where T : Enum
        {
            Type type = Enum.GetUnderlyingType(typeof(T));

            if (type == typeof(byte))
            {
                return (T)Enum.ToObject(typeof(T), ReadByte());
            }
            else if (type == typeof(sbyte))
            {
                return (T)Enum.ToObject(typeof(T), ReadSByte());
            }
            else if (type == typeof(short))
            {
                return (T)Enum.ToObject(typeof(T), ReadShort());
            }
            else if (type == typeof(ushort))
            {
                return (T)Enum.ToObject(typeof(T), ReadUShort());
            }
            else if (type == typeof(int))
            {
                return (T)Enum.ToObject(typeof(T), ReadInt32());
            }
            else if (type == typeof(uint))
            {
                return (T)Enum.ToObject(typeof(T), ReadUInt32());
            }
            else if (type == typeof(long) || type == typeof(IntPtr))
            {
                return (T)Enum.ToObject(typeof(T), ReadLong());
            }
            else if (type == typeof(ulong) || type == typeof(UIntPtr))
            {
                return (T)Enum.ToObject(typeof(T), ReadULong());
            }
            else
            {
                // This should never happen
                throw new ReaderException($"Enum type unknown ({type})");
            }
        }

        private int IncrementIndex(int size)
        {
            int i = Position;
            Position += size;
            return i;
        }

        public void Seek(int position)
        {
            Position = position;
        }

        internal byte[] GetBuffer()
        {
            return array;
        }
    }

    public class ReaderException(string msg) : Exception(msg);

}
