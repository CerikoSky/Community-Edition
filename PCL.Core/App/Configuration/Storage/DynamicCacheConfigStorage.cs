using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private readonly HashSet<object> _pending = [];
    private readonly object CacheLock = new();
    private ConfigStorage? _nullContextCache;
    private bool _stopping;

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        ConfigStorage? storage;
        lock (CacheLock)
        {
            if (context is null) storage = _nullContextCache;
            else _cache.TryGetValue(context, out storage);
        }
        if (storage is null)
        {
            lock (CacheLock) _pending.Add(context ?? "");
            ConfigStorage? created;
            try
            {
                created = StorageFactory(context);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to invoke storage factory", ex);
            }
            lock (CacheLock)
            {
                _pending.Remove(context ?? "");
                Monitor.PulseAll(CacheLock);
                var replaced = _stopping
                    || (context is null ? _nullContextCache : _cache.GetValueOrDefault(context)) is not null;
                if (replaced) created.Stop();
                else if (context is null) storage = _nullContextCache = created;
                else storage = _cache[context] = created;
            }
        }
        return storage.Access(action, ref key, ref value, context);
    }

    protected override void OnStop()
    {
        ConfigStorage[] storages;
        lock (CacheLock)
        {
            _stopping = true;
            while (_pending.Count > 0) Monitor.Wait(CacheLock);
            storages = _nullContextCache is null ? [.. _cache.Values] : [.. _cache.Values, _nullContextCache];
            _nullContextCache = null;
            _cache.Clear();
        }
        foreach (var item in storages) item.Stop();
    }

    public bool InvalidateCache(object context)
    {
        ConfigStorage? center;
        lock (CacheLock)
        {
            while (_pending.Contains(context ?? "")) Monitor.Wait(CacheLock);
            if (context is null)
            {
                center = _nullContextCache;
                _nullContextCache = null;
            }
            else if (!_cache.Remove(context, out center)) return false;
        }
        center?.Stop();
        return true;
    }
}
