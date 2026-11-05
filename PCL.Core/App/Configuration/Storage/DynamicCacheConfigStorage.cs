using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private readonly object CacheLock = new();
    private ConfigStorage? _nullContextCache;

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
            if (storage is null)
            {
                try
                {
                    storage = StorageFactory(context);
                    if (context is null) _nullContextCache = storage;
                    else _cache[context] = storage;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to invoke storage factory", ex);
                }
            }
        }
        return storage.Access(action, ref key, ref value, context);
    }

    protected override void OnStop()
    {
        ConfigStorage[] storages;
        lock (CacheLock)
        {
            storages = [.. _cache.Values];
            _cache.Clear();
        }
        foreach (var item in storages) item.Stop();
    }

    public bool InvalidateCache(object context)
    {
        ConfigStorage? center;
        lock (CacheLock)
        {
            if (!_cache.Remove(context, out center)) return false;
        }
        center.Stop();
        return true;
    }
}
