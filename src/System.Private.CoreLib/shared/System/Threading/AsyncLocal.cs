// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace System.Threading
{
    //
    // AsyncLocal<T> represents "ambient" data that is local to a given asynchronous control flow, such as an
    // async method.  For example, say you want to associate a culture with a given async flow:
    //
    // static AsyncLocal<Culture> s_currentCulture = new AsyncLocal<Culture>();
    //
    // static async Task SomeOperationAsync(Culture culture)
    // {
    //    s_currentCulture.Value = culture;
    //
    //    await FooAsync();
    // }
    //
    // static async Task FooAsync()
    // {
    //    PrintStringWithCulture(s_currentCulture.Value);
    // }
    //
    // AsyncLocal<T> also provides optional notifications when the value associated with the current thread
    // changes, either because it was explicitly changed by setting the Value property, or implicitly changed
    // when the thread encountered an "await" or other context transition.  For example, we might want our
    // current culture to be communicated to the OS as well:
    //
    // static AsyncLocal<Culture> s_currentCulture = new AsyncLocal<Culture>(
    //   args =>
    //   {
    //      NativeMethods.SetThreadCulture(args.CurrentValue.LCID);
    //   });
    //
    public sealed class AsyncLocal<T> : IAsyncLocal
    {
        private readonly Action<AsyncLocalValueChangedArgs<T>>? m_valueChangedHandler;

        //
        // Constructs an AsyncLocal<T> that does not receive change notifications.
        //
        public AsyncLocal()
        {
        }

        //
        // Constructs an AsyncLocal<T> with a delegate that is called whenever the current value changes
        // on any thread.
        //
        public AsyncLocal(Action<AsyncLocalValueChangedArgs<T>>? valueChangedHandler)
        {
            m_valueChangedHandler = valueChangedHandler;
        }

        [MaybeNull]
        public T Value
        {
            get
            {
                object? obj = ExecutionContext.GetLocalValue(this);
                return (obj == null) ? default : (T)obj;
            }
            set
            {
                ExecutionContext.SetLocalValue(this, value, m_valueChangedHandler != null);
            }
        }

        void IAsyncLocal.OnValueChanged(object? previousValueObj, object? currentValueObj, bool contextChanged)
        {
            Debug.Assert(m_valueChangedHandler != null);
            T previousValue = previousValueObj == null ? default! : (T)previousValueObj;
            T currentValue = currentValueObj == null ? default! : (T)currentValueObj;
            m_valueChangedHandler(new AsyncLocalValueChangedArgs<T>(previousValue, currentValue, contextChanged));
        }
    }

    //
    // Interface to allow non-generic code in ExecutionContext to call into the generic AsyncLocal<T> type.
    //
    internal interface IAsyncLocal
    {
        void OnValueChanged(object? previousValue, object? currentValue, bool contextChanged);
    }

    public readonly struct AsyncLocalValueChangedArgs<T>
    {
        [MaybeNull] public T PreviousValue { get; }
        [MaybeNull] public T CurrentValue { get; }

        //
        // If the value changed because we changed to a different ExecutionContext, this is true.  If it changed
        // because someone set the Value property, this is false.
        //
        public bool ThreadContextChanged { get; }

        internal AsyncLocalValueChangedArgs([AllowNull] T previousValue, [AllowNull] T currentValue, bool contextChanged)
        {
            PreviousValue = previousValue;
            CurrentValue = currentValue;
            ThreadContextChanged = contextChanged;
        }
    }

    //
    // Interface used to store an IAsyncLocal => object mapping in ExecutionContext.
    // Implementations are specialized based on the number of elements in the immutable
    // map in order to minimize memory consumption and look-up times.
    //
    internal interface IAsyncLocalValueMap
    {
        bool TryGetValue(IAsyncLocal key, out object? value);
        IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent);
        IAsyncLocalValueMap TryClone();
    }

    //
    // Utility functions for getting/creating instances of IAsyncLocalValueMap
    //
    internal static class AsyncLocalValueMap
    {
        private const string AsyncLocalCloneMethodName = "_AsyncLocalClone_";
        private static readonly Dictionary<Type, Func<object, object>?> AsyncLocalCloneMethodCache = new Dictionary<Type, Func<object, object>?>();

        private static object TryClone(this object value)
        {
            lock (AsyncLocalCloneMethodCache)
            {
                var type = value.GetType();

                if (!AsyncLocalCloneMethodCache.TryGetValue(type, out var cloneDelegate))
                {
                    var methodInfo = type.GetMethod(AsyncLocalCloneMethodName, BindingFlags.Static | BindingFlags.Public, null, new Type[] {typeof(object)}, null);
                    cloneDelegate = (Func<object, object>?)methodInfo?.CreateDelegate(typeof(Func<object, object>));
                    AsyncLocalCloneMethodCache.TryAdd(type, cloneDelegate);
                }

                return cloneDelegate == null ? value : cloneDelegate(value);
            }
        }

        public static IAsyncLocalValueMap Empty { get; } = new EmptyAsyncLocalValueMap();

        public static bool IsEmpty(IAsyncLocalValueMap asyncLocalValueMap)
        {
            Debug.Assert(asyncLocalValueMap != null);
            Debug.Assert(asyncLocalValueMap == Empty || asyncLocalValueMap.GetType() != typeof(EmptyAsyncLocalValueMap));

            return asyncLocalValueMap == Empty;
        }

        public static IAsyncLocalValueMap Create(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
        {
            // If the value isn't null or a null value may not be treated as nonexistent, then create a new one-element map
            // to store the key/value pair.  Otherwise, use the empty map.
            return value != null || !treatNullValueAsNonexistent ?
                new OneElementAsyncLocalValueMap(key, value) :
                Empty;
        }

        // Instance without any key/value pairs.  Used as a singleton/
        private sealed class EmptyAsyncLocalValueMap : IAsyncLocalValueMap
        {
            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                // If the value isn't null or a null value may not be treated as nonexistent, then create a new one-element map
                // to store the key/value pair.  Otherwise, use the empty map.
                return value != null || !treatNullValueAsNonexistent ?
                    new OneElementAsyncLocalValueMap(key, value) :
                    (IAsyncLocalValueMap)this;
            }

            public bool TryGetValue(IAsyncLocal key, out object? value)
            {
                value = null;
                return false;
            }

            public IAsyncLocalValueMap TryClone()
            {
                return this;
            }
        }

        // Instance with one key/value pair.
        private sealed class OneElementAsyncLocalValueMap : IAsyncLocalValueMap
        {
            private readonly IAsyncLocal _key1;
            private readonly object? _value1;

            public OneElementAsyncLocalValueMap(IAsyncLocal key, object? value)
            {
                _key1 = key; _value1 = value;
            }

            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                if (value != null || !treatNullValueAsNonexistent)
                {
                    // If the key matches one already contained in this map, then create a new one-element map with the updated
                    // value, otherwise create a two-element map with the additional key/value.
                    return ReferenceEquals(key, _key1) ?
                        new OneElementAsyncLocalValueMap(key, value) :
                        (IAsyncLocalValueMap)new TwoElementAsyncLocalValueMap(_key1, _value1, key, value);
                }
                else
                {
                    // If the key exists in this map, remove it by downgrading to an empty map.  Otherwise, there's nothing to
                    // add or remove, so just return this map.
                    return ReferenceEquals(key, _key1) ?
                        Empty :
                        (IAsyncLocalValueMap)this;
                }
            }

            public bool TryGetValue(IAsyncLocal key, out object? value)
            {
                if (ReferenceEquals(key, _key1))
                {
                    value = _value1;
                    return true;
                }
                else
                {
                    value = null;
                    return false;
                }
            }

            public IAsyncLocalValueMap TryClone()
            {
                var value1Clone = _value1?.TryClone();
                return ReferenceEquals(value1Clone, _value1) ? this : new OneElementAsyncLocalValueMap(_key1, value1Clone);
            }
        }

        // Instance with two key/value pairs.
        private sealed class TwoElementAsyncLocalValueMap : IAsyncLocalValueMap
        {
            private readonly IAsyncLocal _key1, _key2;
            private readonly object? _value1, _value2;

            public TwoElementAsyncLocalValueMap(IAsyncLocal key1, object? value1, IAsyncLocal key2, object? value2)
            {
                _key1 = key1; _value1 = value1;
                _key2 = key2; _value2 = value2;
            }

            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                if (value != null || !treatNullValueAsNonexistent)
                {
                    // If the key matches one already contained in this map, then create a new two-element map with the updated
                    // value, otherwise create a three-element map with the additional key/value.
                    return
                        ReferenceEquals(key, _key1) ? new TwoElementAsyncLocalValueMap(key, value, _key2, _value2) :
                        ReferenceEquals(key, _key2) ? new TwoElementAsyncLocalValueMap(_key1, _value1, key, value) :
                        (IAsyncLocalValueMap)new ThreeElementAsyncLocalValueMap(_key1, _value1, _key2, _value2, key, value);
                }
                else
                {
                    // If the key exists in this map, remove it by downgrading to a one-element map without the key.  Otherwise,
                    // there's nothing to add or remove, so just return this map.
                    return
                        ReferenceEquals(key, _key1) ? new OneElementAsyncLocalValueMap(_key2, _value2) :
                        ReferenceEquals(key, _key2) ? new OneElementAsyncLocalValueMap(_key1, _value1) :
                        (IAsyncLocalValueMap)this;
                }
            }

            public bool TryGetValue(IAsyncLocal key, out object? value)
            {
                if (ReferenceEquals(key, _key1))
                {
                    value = _value1;
                    return true;
                }
                else if (ReferenceEquals(key, _key2))
                {
                    value = _value2;
                    return true;
                }
                else
                {
                    value = null;
                    return false;
                }
            }

            public IAsyncLocalValueMap TryClone()
            {
                var value1Clone = _value1?.TryClone();
                var value2Clone = _value2?.TryClone();

                return ReferenceEquals(value1Clone, _value1) && ReferenceEquals(value2Clone, _value2)
                    ? this
                    : new TwoElementAsyncLocalValueMap(_key1, value1Clone, _key2, value2Clone);
            }
        }

        // Instance with three key/value pairs.
        private sealed class ThreeElementAsyncLocalValueMap : IAsyncLocalValueMap
        {
            private readonly IAsyncLocal _key1, _key2, _key3;
            private readonly object? _value1, _value2, _value3;

            public ThreeElementAsyncLocalValueMap(IAsyncLocal key1, object? value1, IAsyncLocal key2, object? value2, IAsyncLocal key3, object? value3)
            {
                _key1 = key1; _value1 = value1;
                _key2 = key2; _value2 = value2;
                _key3 = key3; _value3 = value3;
            }

            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                if (value != null || !treatNullValueAsNonexistent)
                {
                    // If the key matches one already contained in this map, then create a new three-element map with the
                    // updated value.
                    if (ReferenceEquals(key, _key1)) return new ThreeElementAsyncLocalValueMap(key, value, _key2, _value2, _key3, _value3);
                    if (ReferenceEquals(key, _key2)) return new ThreeElementAsyncLocalValueMap(_key1, _value1, key, value, _key3, _value3);
                    if (ReferenceEquals(key, _key3)) return new ThreeElementAsyncLocalValueMap(_key1, _value1, _key2, _value2, key, value);

                    // The key doesn't exist in this map, so upgrade to a multi map that contains
                    // the additional key/value pair.
                    var multi = new MultiElementAsyncLocalValueMap(4);
                    multi.UnsafeStore(0, _key1, _value1);
                    multi.UnsafeStore(1, _key2, _value2);
                    multi.UnsafeStore(2, _key3, _value3);
                    multi.UnsafeStore(3, key, value);
                    return multi;
                }
                else
                {
                    // If the key exists in this map, remove it by downgrading to a two-element map without the key.  Otherwise,
                    // there's nothing to add or remove, so just return this map.
                    return
                        ReferenceEquals(key, _key1) ? new TwoElementAsyncLocalValueMap(_key2, _value2, _key3, _value3) :
                        ReferenceEquals(key, _key2) ? new TwoElementAsyncLocalValueMap(_key1, _value1, _key3, _value3) :
                        ReferenceEquals(key, _key3) ? new TwoElementAsyncLocalValueMap(_key1, _value1, _key2, _value2) :
                        (IAsyncLocalValueMap)this;
                }
            }

            public bool TryGetValue(IAsyncLocal key, out object? value)
            {
                if (ReferenceEquals(key, _key1))
                {
                    value = _value1;
                    return true;
                }
                else if (ReferenceEquals(key, _key2))
                {
                    value = _value2;
                    return true;
                }
                else if (ReferenceEquals(key, _key3))
                {
                    value = _value3;
                    return true;
                }
                else
                {
                    value = null;
                    return false;
                }
            }

            public IAsyncLocalValueMap TryClone()
            {
                var value1Clone = _value1?.TryClone();
                var value2Clone = _value2?.TryClone();
                var value3Clone = _value3?.TryClone();

                return ReferenceEquals(value1Clone, _value1) && ReferenceEquals(value2Clone, _value2) && ReferenceEquals(value3Clone, _value3)
                    ? this
                    : new ThreeElementAsyncLocalValueMap(_key1, value1Clone, _key2, value2Clone, _key3, value3Clone);
            }
        }

        // Instance with up to 16 key/value pairs.
        private sealed class MultiElementAsyncLocalValueMap : IAsyncLocalValueMap
        {
            internal const int MaxMultiElements = 16;
            private readonly KeyValuePair<IAsyncLocal, object?>[] _keyValues;

            internal MultiElementAsyncLocalValueMap(int count)
            {
                Debug.Assert(count <= MaxMultiElements);
                _keyValues = new KeyValuePair<IAsyncLocal, object?>[count];
            }

            internal void UnsafeStore(int index, IAsyncLocal key, object? value)
            {
                Debug.Assert(index < _keyValues.Length);
                _keyValues[index] = new KeyValuePair<IAsyncLocal, object?>(key, value);
            }

            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                // Find the key in this map.
                for (int i = 0; i < _keyValues.Length; i++)
                {
                    if (ReferenceEquals(key, _keyValues[i].Key))
                    {
                        // The key is in the map.
                        if (value != null || !treatNullValueAsNonexistent)
                        {
                            // Create a new map of the same size that has all of the same pairs, with this new key/value pair
                            // overwriting the old.
                            var multi = new MultiElementAsyncLocalValueMap(_keyValues.Length);
                            Array.Copy(_keyValues, 0, multi._keyValues, 0, _keyValues.Length);
                            multi._keyValues[i] = new KeyValuePair<IAsyncLocal, object?>(key, value);
                            return multi;
                        }
                        else if (_keyValues.Length == 4)
                        {
                            // We only have four elements, one of which we're removing, so downgrade to a three-element map,
                            // without the matching element.
                            return
                                i == 0 ? new ThreeElementAsyncLocalValueMap(_keyValues[1].Key, _keyValues[1].Value, _keyValues[2].Key, _keyValues[2].Value, _keyValues[3].Key, _keyValues[3].Value) :
                                i == 1 ? new ThreeElementAsyncLocalValueMap(_keyValues[0].Key, _keyValues[0].Value, _keyValues[2].Key, _keyValues[2].Value, _keyValues[3].Key, _keyValues[3].Value) :
                                i == 2 ? new ThreeElementAsyncLocalValueMap(_keyValues[0].Key, _keyValues[0].Value, _keyValues[1].Key, _keyValues[1].Value, _keyValues[3].Key, _keyValues[3].Value) :
                                (IAsyncLocalValueMap)new ThreeElementAsyncLocalValueMap(_keyValues[0].Key, _keyValues[0].Value, _keyValues[1].Key, _keyValues[1].Value, _keyValues[2].Key, _keyValues[2].Value);
                        }
                        else
                        {
                            // We have enough elements remaining to warrant a multi map.  Create a new one and copy all of the
                            // elements from this one, except the one to be removed.
                            var multi = new MultiElementAsyncLocalValueMap(_keyValues.Length - 1);
                            if (i != 0) Array.Copy(_keyValues, 0, multi._keyValues, 0, i);
                            if (i != _keyValues.Length - 1) Array.Copy(_keyValues, i + 1, multi._keyValues, i, _keyValues.Length - i - 1);
                            return multi;
                        }
                    }
                }

                // The key does not already exist in this map.

                if (value == null && treatNullValueAsNonexistent)
                {
                    // We can simply return this same map, as there's nothing to add or remove.
                    return this;
                }

                // We need to create a new map that has the additional key/value pair.
                // If with the addition we can still fit in a multi map, create one.
                if (_keyValues.Length < MaxMultiElements)
                {
                    var multi = new MultiElementAsyncLocalValueMap(_keyValues.Length + 1);
                    Array.Copy(_keyValues, 0, multi._keyValues, 0, _keyValues.Length);
                    multi._keyValues[_keyValues.Length] = new KeyValuePair<IAsyncLocal, object?>(key, value);
                    return multi;
                }

                // Otherwise, upgrade to a many map.
                var many = new ManyElementAsyncLocalValueMap(MaxMultiElements + 1);
                foreach (KeyValuePair<IAsyncLocal, object?> pair in _keyValues)
                {
                    many[pair.Key] = pair.Value;
                }
                many[key] = value;
                return many;
            }

            public bool TryGetValue(IAsyncLocal key, out object? value)
            {
                foreach (KeyValuePair<IAsyncLocal, object?> pair in _keyValues)
                {
                    if (ReferenceEquals(key, pair.Key))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
                value = null;
                return false;
            }

            public IAsyncLocalValueMap TryClone()
            {
                var cloneHappened = false;
                var clone = new MultiElementAsyncLocalValueMap(_keyValues.Length);

                for (var i = 0; i < _keyValues.Length; i++)
                {
                    var kvp = _keyValues[i];
                    var valueClone = kvp.Value?.TryClone();

                    if (!ReferenceEquals(valueClone, kvp.Value))
                        cloneHappened = true;

                    clone._keyValues[i] = new KeyValuePair<IAsyncLocal, object?>(kvp.Key, valueClone);
                }

                return cloneHappened ? clone : this;
            }
        }

        // Instance with any number of key/value pairs.
        private sealed class ManyElementAsyncLocalValueMap : Dictionary<IAsyncLocal, object?>, IAsyncLocalValueMap
        {
            public ManyElementAsyncLocalValueMap(int capacity) : base(capacity) { }

            public IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent)
            {
                int count = Count;
                bool containsKey = ContainsKey(key);

                // If the value being set exists, create a new many map, copy all of the elements from this one,
                // and then store the new key/value pair into it.  This is the most common case.
                if (value != null || !treatNullValueAsNonexistent)
                {
                    var map = new ManyElementAsyncLocalValueMap(count + (containsKey ? 0 : 1));
                    foreach (KeyValuePair<IAsyncLocal, object?> pair in this)
                    {
                        map[pair.Key] = pair.Value;
                    }
                    map[key] = value;
                    return map;
                }

                // Otherwise, the value is null and a null value may be treated as nonexistent. We can downgrade to a smaller
                // map rather than storing null.

                // If the key is contained in this map, we're going to create a new map that's one pair smaller.
                if (containsKey)
                {
                    // If the new count would be within range of a multi map instead of a many map,
                    // downgrade to the multi map, which uses less memory and is faster to access.
                    // Otherwise, just create a new many map that's missing this key.
                    if (count == MultiElementAsyncLocalValueMap.MaxMultiElements + 1)
                    {
                        var multi = new MultiElementAsyncLocalValueMap(MultiElementAsyncLocalValueMap.MaxMultiElements);
                        int index = 0;
                        foreach (KeyValuePair<IAsyncLocal, object?> pair in this)
                        {
                            if (!ReferenceEquals(key, pair.Key))
                            {
                                multi.UnsafeStore(index++, pair.Key, pair.Value);
                            }
                        }
                        Debug.Assert(index == MultiElementAsyncLocalValueMap.MaxMultiElements);
                        return multi;
                    }
                    else
                    {
                        var map = new ManyElementAsyncLocalValueMap(count - 1);
                        foreach (KeyValuePair<IAsyncLocal, object?> pair in this)
                        {
                            if (!ReferenceEquals(key, pair.Key))
                            {
                                map[pair.Key] = pair.Value;
                            }
                        }
                        Debug.Assert(map.Count == count - 1);
                        return map;
                    }
                }

                // We were storing null and a null value may be treated as nonexistent, but the key wasn't in the map, so
                // there's nothing to change.  Just return this instance.
                return this;
            }

            public IAsyncLocalValueMap TryClone()
            {
                var cloneHappened = false;
                var clone = new ManyElementAsyncLocalValueMap(Count);

                foreach (var kvp in this)
                {
                    var valueClone = kvp.Value?.TryClone();

                    if (!ReferenceEquals(valueClone, kvp.Value))
                        cloneHappened = true;

                    clone[kvp.Key] = valueClone;
                }

                return cloneHappened ? clone : this;
            }
        }
    }
}
