using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace Multiplayer.Common
{
    /// <summary>
    /// Caches compiled parameterless-constructor delegates so that hot sync deserialization
    /// paths can avoid the per-call cost of <see cref="Activator.CreateInstance(Type)"/>.
    /// </summary>
    public static class ConstructorCache
    {
        private static readonly ConcurrentDictionary<Type, Func<object>> parameterless = new();

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
    }
}
