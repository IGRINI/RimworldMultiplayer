using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Multiplayer.Common
{
    /// <summary>
    /// Caches compiled constructor delegates so that hot sync deserialization paths can avoid
    /// the per-call cost of <see cref="Activator.CreateInstance(Type)"/>.
    /// </summary>
    public static class ConstructorCache
    {
        private static readonly ConcurrentDictionary<Type, Func<object>> parameterless = new();
        private static readonly ConcurrentDictionary<Type, Func<int, object>> listIntCtors = new();
        private static readonly ConcurrentDictionary<Type, Func<object, object>> hashSetEnumerableCtors = new();

        /// <summary>
        /// Creates a new instance of <paramref name="type"/> using its parameterless constructor.
        /// On the first call for a type a <see cref="DynamicMethod"/> is compiled and cached;
        /// subsequent calls reuse the compiled delegate.
        /// </summary>
        public static object CreateInstance(Type type)
        {
            return GetOrAddFactory(type)();
        }

        /// <summary>
        /// Returns the cached factory delegate for <paramref name="type"/>, compiling it if necessary.
        /// Exposed for callers that want to avoid a dictionary lookup in tight loops.
        /// </summary>
        public static Func<object> GetOrAddFactory(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return parameterless.GetOrAdd(type, BuildFactory);
        }

        /// <summary>
        /// Fast path for <c>List&lt;T&gt;(int capacity)</c>. The IL-emitted factory is keyed by
        /// element type; the resulting <see cref="IList"/> can be filled by the caller.
        /// </summary>
        public static object CreateList(Type elementType, int capacity)
        {
            if (elementType == null)
                throw new ArgumentNullException(nameof(elementType));

            return listIntCtors.GetOrAdd(elementType, BuildListIntFactory)(capacity);
        }

        /// <summary>
        /// Fast path for <c>HashSet&lt;T&gt;(IEnumerable&lt;T&gt;)</c>. The IL-emitted factory is
        /// keyed by element type; <paramref name="enumerable"/> must be assignable to
        /// <c>IEnumerable&lt;T&gt;</c>.
        /// </summary>
        public static object CreateHashSet(Type elementType, object enumerable)
        {
            if (elementType == null)
                throw new ArgumentNullException(nameof(elementType));

            return hashSetEnumerableCtors.GetOrAdd(elementType, BuildHashSetEnumerableFactory)(enumerable);
        }

        private static Func<object> BuildFactory(Type type)
        {
            // Activator handles value types (which have an implicit parameterless ctor) and
            // any other edge cases we don't try to emit IL for.
            if (type.IsValueType)
                return () => Activator.CreateInstance(type)!;

            var ctor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (ctor == null)
                return () => Activator.CreateInstance(type, nonPublic: true)!;

            var dynMethod = new DynamicMethod(
                $"ConstructorCache_Create_{type.Name}",
                typeof(object),
                Type.EmptyTypes,
                typeof(ConstructorCache).Module,
                skipVisibility: true);

            var il = dynMethod.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);

            return (Func<object>)dynMethod.CreateDelegate(typeof(Func<object>));
        }

        private static Func<int, object> BuildListIntFactory(Type elementType)
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var ctor = listType.GetConstructor(new[] { typeof(int) })
                ?? throw new InvalidOperationException($"List<{elementType}> has no (int) ctor");

            var dynMethod = new DynamicMethod(
                $"ConstructorCache_CreateList_{elementType.Name}",
                typeof(object),
                new[] { typeof(int) },
                typeof(ConstructorCache).Module,
                skipVisibility: true);

            var il = dynMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);

            return (Func<int, object>)dynMethod.CreateDelegate(typeof(Func<int, object>));
        }

        private static Func<object, object> BuildHashSetEnumerableFactory(Type elementType)
        {
            var setType = typeof(HashSet<>).MakeGenericType(elementType);
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
            var ctor = setType.GetConstructor(new[] { enumerableType })
                ?? throw new InvalidOperationException($"HashSet<{elementType}> has no (IEnumerable<T>) ctor");

            var dynMethod = new DynamicMethod(
                $"ConstructorCache_CreateHashSet_{elementType.Name}",
                typeof(object),
                new[] { typeof(object) },
                typeof(ConstructorCache).Module,
                skipVisibility: true);

            var il = dynMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, enumerableType);
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);

            return (Func<object, object>)dynMethod.CreateDelegate(typeof(Func<object, object>));
        }
    }
}
