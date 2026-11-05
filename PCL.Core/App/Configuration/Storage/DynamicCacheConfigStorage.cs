using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using PCL.Core.Logging;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private sealed class Entry(ConfigStorage storage)
    {
        public required object Key { get; init; }
        public ConfigStorage Storage { get; } = storage;
        public int Users;
        public bool Removed;
        public bool Stopped;
    }

    private readonly Dictionary<object, Entry> _cache = [];
    private readonly object CacheLock = new();
    private bool _stopping;

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    // 三个方法都要求在 CacheLock 内调用
    private Entry? _Borrow(object key)
    {
        if (!_cache.TryGetValue(key, out var entry) || entry.Removed) return null;
        entry.Users++;
        return entry;
    }

    private void _Release(Entry entry)
    {
        bool stop;
        lock (CacheLock)
        {
            entry.Users--;
            stop = entry.Users == 0 && entry.Removed && !entry.Stopped;
            if (!stop) return;
            entry.Stopped = true;
        }
        try
        {
            entry.Storage.Stop();
        }
        catch
        {
            lock (CacheLock)
            {
                if (_stopping) throw;
                entry.Stopped = false;
                entry.Removed = false;
                _cache[entry.Key] = entry;
            }
            throw;
        }
        finally
        {
            lock (CacheLock) Monitor.PulseAll(CacheLock);
        }
    }

    private void _Retire(Entry entry)
    {
        entry.Removed = true;
        if (entry.Users > 0) return;
        entry.Stopped = true;
        try
        {
            entry.Storage.Stop();
        }
        catch
        {
            entry.Stopped = false;
            entry.Removed = false;
            _cache[entry.Key] = entry;
            throw;
        }
    }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        var cacheKey = (object?)context ?? "";
        Entry? entry;
        lock (CacheLock) entry = _Borrow(cacheKey);
        if (entry is null)
        {
            ConfigStorage createdStorage;
            try
            {
                createdStorage = StorageFactory(context);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to invoke storage factory", ex);
            }

            var created = new Entry(createdStorage) { Key = cacheKey };
            lock (CacheLock)
            {
                if (_stopping) return false;
                // 旧存储停止前不能新建，否则两份快照会互相覆盖
                while (_cache.TryGetValue(cacheKey, out var stopping) && stopping.Removed && !stopping.Stopped)
                    Monitor.Wait(CacheLock);
                if (_cache.TryGetValue(cacheKey, out var existing) && !existing.Removed)
                {
                    existing.Users++;
                    entry = existing;
                }
                else
                {
                    created.Users = 1;
                    _cache[cacheKey] = created;
                    entry = created;
                }
            }
        }

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
            entries = [.. _cache.Values];
            _cache.Clear();
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
            if (!stop) continue;
            try
            {
                entry.Storage.Stop();
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "Config", "配置存储停止失败");
            }
        }
        lock (CacheLock) Monitor.PulseAll(CacheLock);
    }

    public bool InvalidateCache(object context)
    {
        Entry? entry;
        lock (CacheLock)
        {
            if (!_cache.Remove(context, out entry)) return false;
            _Retire(entry);
            Monitor.PulseAll(CacheLock);
        }
        return true;
    }
}
