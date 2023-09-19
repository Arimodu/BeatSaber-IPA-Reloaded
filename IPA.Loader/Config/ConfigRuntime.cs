﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using IPA.Utilities.Async;
using System.IO;
using Logger = IPA.Logging.Logger;
#if NET4
using Task = System.Threading.Tasks.Task;
using TaskEx = System.Threading.Tasks.Task;
#endif

namespace IPA.Config
{
    internal static class ConfigRuntime
    {
        private class DirInfoEqComparer : IEqualityComparer<DirectoryInfo>
        {
            public bool Equals(DirectoryInfo x, DirectoryInfo y)
                => x?.FullName == y?.FullName;

            public int GetHashCode(DirectoryInfo obj)
                => obj?.GetHashCode() ?? 0;
        }

        private static readonly ConcurrentBag<Config> configs = new ConcurrentBag<Config>();
        private static readonly Action configsChangedWatcher = () =>
        {
            foreach (var config in configs.Where(c => c.Store != null).ToArray())
            {
                config.Store.SyncAction = () => RequiresSave.Add(() => Save(config)); // Create and save the nested actions,
                                                                                      // top action will head over to the config itself, replacing SyncObject
                                                                                      // Inner action will be invoked by the SaveThread to save the actual config
            }
        };
        private static readonly ConcurrentDictionary<DirectoryInfo, FileSystemWatcher> watchers 
            = new ConcurrentDictionary<DirectoryInfo, FileSystemWatcher>(new DirInfoEqComparer());
        private static readonly ConcurrentDictionary<FileSystemWatcher, ConcurrentBag<Config>> watcherTrackConfigs
            = new ConcurrentDictionary<FileSystemWatcher, ConcurrentBag<Config>>();
        private static SingleThreadTaskScheduler loadScheduler = null;
        private static TaskFactory loadFactory = null;
        private static Thread saveThread = null;

        private static void TryStartRuntime()
        {
            if (loadScheduler == null || !loadScheduler.IsRunning)
            {
                loadFactory = null;
                loadScheduler = new SingleThreadTaskScheduler();
                loadScheduler.Start();
            }
            if (loadFactory == null)
                loadFactory = new TaskFactory(loadScheduler);
            if (saveThread == null || !saveThread.IsAlive)
            {
                saveThread = new Thread(SaveThread);
                saveThread.Start();
            }

            AppDomain.CurrentDomain.ProcessExit -= ShutdownRuntime;
            AppDomain.CurrentDomain.ProcessExit += ShutdownRuntime;
        }

        private static void ShutdownRuntime(object sender, EventArgs e)
            => ShutdownRuntime();
        internal static void ShutdownRuntime()
        {
            try
            {
                watcherTrackConfigs.Clear();
                var watchList = watchers.ToArray();
                watchers.Clear();

                foreach (var pair in watchList)
                    pair.Value.EnableRaisingEvents = false;

                loadScheduler.Join(); // we can wait for the loads to finish
                saveThread.Abort(); // eww, but i don't like any of the other potential solutions

                SaveAll();
            }
            catch 
            {
            }
        }

        public static void RegisterConfig(Config cfg)
        {
            lock (configs)
            { // we only lock this segment, so that this only waits on other calls to this
                if (configs.ToArray().Contains(cfg))
                    throw new InvalidOperationException("Config already registered to runtime!");

                configs.Add(cfg);
            }
            configsChangedWatcher?.Invoke();

            TryStartRuntime();

            AddConfigToWatchers(cfg);
        }

        public static void ConfigChanged()
        {
            configsChangedWatcher.Invoke();
        }

        private static void AddConfigToWatchers(Config config)
        {
            var dir = config.File.Directory;
            if (!watchers.TryGetValue(dir, out var watcher))
            { // create the watcher
                watcher = watchers.GetOrAdd(dir, dir => new FileSystemWatcher(dir.FullName));

                watcher.NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.LastAccess
                    | NotifyFilters.Attributes
                    | NotifyFilters.CreationTime;

                watcher.Changed += FileChangedEvent;
                watcher.Created += FileChangedEvent;
                watcher.Renamed += FileChangedEvent;
                watcher.Deleted += FileChangedEvent;
            }

            TryStartRuntime();

            watcher.EnableRaisingEvents = false; // disable while we do shit

            var bag = watcherTrackConfigs.GetOrAdd(watcher, w => new ConcurrentBag<Config>());
            // we don't need to check containment because this function will only be called once per config ever
            bag.Add(config);

            watcher.EnableRaisingEvents = true;
        }

        private static void EnsureWritesSane(Config config)
        {
            // compare exchange loop to be sane
            var writes = config.Writes;
            while (writes < 0)
                writes = Interlocked.CompareExchange(ref config.Writes, 0, writes);
        }

        private static void FileChangedEvent(object sender, FileSystemEventArgs e)
        {
            var watcher = sender as FileSystemWatcher;
            if (!watcherTrackConfigs.TryGetValue(watcher, out var bag)) return;

            var config = bag.FirstOrDefault(c => c.File.FullName == e.FullPath);
            if (config != null && Interlocked.Decrement(ref config.Writes) + 1 <= 0)
            {
                EnsureWritesSane(config);
                TriggerFileLoad(config);
            }
        }

        public static Task TriggerFileLoad(Config config)
            => loadFactory.StartNew(() => LoadTask(config));

        public static Task TriggerLoadAll()
            => TaskEx.WhenAll(configs.Select(TriggerFileLoad));

        /// <summary>
        /// this is synchronous, unlike <see cref="TriggerFileLoad(Config)"/>
        /// </summary>
        /// <param name="config"></param>
        public static void Save(Config config)
        {
            var store = config.Store;

            try
            {
                using var readLock = Synchronization.LockRead(store.WriteSyncObject);

                EnsureWritesSane(config);
                Interlocked.Increment(ref config.Writes);
                store.WriteTo(config.configProvider);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Config.Error($"{nameof(IConfigStore)} for {config.File} errored while writing to disk");
                Logger.Config.Error(e);
            }
        }

        /// <summary>
        /// this is synchronous, unlike <see cref="TriggerLoadAll"/>
        /// </summary>
        public static void SaveAll()
        {
            foreach (var config in configs)
                Save(config);
        }

        private static void LoadTask(Config config)
        { // these tasks will always be running in the same thread as each other
            try
            {
                var store = config.Store;
                using var writeLock = Synchronization.LockWrite(store.WriteSyncObject);
                store.ReadFrom(config.configProvider);
            }
            catch (Exception e)
            {
                Logger.Config.Error($"{nameof(IConfigStore)} for {config.File} errored while reading from the {nameof(IConfigProvider)}");
                Logger.Config.Error(e);
            }
        }

        static readonly BlockingCollection<Action> RequiresSave = new(); // A blocking collection to store what needs to be Saved

        private static void SaveThread()
        {
            try
            {
                var configArr = configs.Where(c => c.Store != null).ToArray();

                configsChangedWatcher.Invoke();

                foreach (var item in RequiresSave.GetConsumingEnumerable())
                {
                    try
                    {
                        item.Invoke(); // Invoke the action
                    }
                    catch (ThreadAbortException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.Config.Error($"Error waiting for in-memory updates");
                        Logger.Config.Error(e);
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // we got aborted :(
            }
            finally
            {
                RequiresSave.Dispose();
            }
        }
    }
}
