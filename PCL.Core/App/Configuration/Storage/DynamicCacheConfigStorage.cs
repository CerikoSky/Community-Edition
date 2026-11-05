using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using PCL.Core.Logging;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private static readonly object NullContextKey = new();

    private sealed class Entry(ConfigStorage storage, object key)
    {
        public ConfigStorage Storage { get; } = storage;
        public object Key { get; } = key;
        public int Users;
        public bool Removed;
        public bool Stopped;
    }

    private readonly Dictionary<object, Entry> _cache = [];
    private readonly object CacheLock = new();
    private readonly HashSet<object> _creating = [];
    private bool _stopping;

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    private static void _Stop(ConfigStorage storage)
    {
        try
        {
            storage.Stop();
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Config", "配置存储停止失败");
        }
    }

    private void _Release(Entry entry)
    {
        ConfigStorage? stop = null;
        lock (CacheLock)
        {
            entry.Users--;
            if (entry.Users == 0 && entry.Removed && !entry.Stopped)
            {
                entry.Stopped = true;
                if (_cache.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    _cache.Remove(entry.Key);
                stop = entry.Storage;
            }
        }
        if (stop is null) return;
        _Stop(stop);
        lock (CacheLock) Monitor.PulseAll(CacheLock);
    }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        var cacheKey = (object?)context ?? NullContextKey;
        Entry? entry;
        while (true)
        {
            lock (CacheLock)
            {
                if (!_stopping && _cache.TryGetValue(cacheKey, out var existing) && !existing.Removed)
                {
                    existing.Users++;
                    entry = existing;
                    break;
                }

                if (_creating.Contains(cacheKey) || (_cache.TryGetValue(cacheKey, out var pending) && pending.Removed && !pending.Stopped))
                {
                    Monitor.Wait(CacheLock);
                    continue;
                }

                if (_stopping)
                {
                    entry = null;
                    break;
                }

                _creating.Add(cacheKey);
            }

            ConfigStorage? created = null;
            try
            {
                created = StorageFactory(context) ?? throw new InvalidOperationException("Storage factory returned null");
            }
            catch (Exception ex)
            {
                lock (CacheLock)
                {
                    _creating.Remove(cacheKey);
                    Monitor.PulseAll(CacheLock);
                }
                throw new Exception("Failed to invoke storage factory", ex);
            }

            lock (CacheLock)
            {
                _creating.Remove(cacheKey);
                Monitor.PulseAll(CacheLock);
                if (_stopping || _cache.ContainsKey(cacheKey))
                {
                    entry = null;
                }
                else
                {
                    entry = new Entry(created, cacheKey) { Users = 1 };
                    _cache[cacheKey] = entry;
                    created = null;
                }
            }

            if (created is not null) _Stop(created);
            break;
        }

        if (entry is null) return false;

        try
        {
            return entry.Storage.Access(action, ref key, ref value, context);
        }
        finally
        {
            _Release(entry);
        }
    }

    protected override void OnStop()
    {
        Entry[] entries;
        lock (CacheLock)
        {
            _stopping = true;
            while (_creating.Count > 0) Monitor.Wait(CacheLock);
            entries = [.. _cache.Values];
            _cache.Clear();
            Monitor.PulseAll(CacheLock);
        }

        foreach (var entry in entries)
        {
            bool stop;
            lock (CacheLock)
            {
                entry.Removed = true;
                stop = entry.Users == 0 && !entry.Stopped;
                if (stop) entry.Stopped = true;
            }
            if (stop) _Stop(entry.Storage);
        }
        lock (CacheLock) Monitor.PulseAll(CacheLock);
    }

    public bool InvalidateCache(object context)
    {
        var cacheKey = (object?)context ?? NullContextKey;
        Entry? entry;
        lock (CacheLock)
        {
            while (_creating.Contains(cacheKey)) Monitor.Wait(CacheLock);
            if (!_cache.TryGetValue(cacheKey, out entry) || entry.Removed) return false;
            entry.Removed = true;
            if (entry.Users > 0) return true;
            _cache.Remove(cacheKey);
            entry.Stopped = true;
        }
        try
        {
            entry.Storage.Stop();
        }
        catch (Exception ex)
        {
            lock (CacheLock)
            {
                entry.Stopped = false;
                entry.Removed = false;
                _cache[cacheKey] = entry;
            }
            throw new Exception("Failed to stop config storage", ex);
        }
        finally
        {
            lock (CacheLock) Monitor.PulseAll(CacheLock);
        }
        return true;
    }
}
