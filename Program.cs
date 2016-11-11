using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using WinSCP;

namespace sync
{
    using EventPool = List<FileSystemEventArgs>;
    using Timer = System.Timers.Timer;

    class Program
    {
        private static EventPool Events
            = new EventPool();

        private static Timer EventTimer = new Timer(100);

        private static string RemoteRoot = "/home/yuraaka/blue/"; //"/home/yuraaka/sync-test/"; //
        private static string LocalRoot = @"C:\dev\blue\arc\"; //@"C:\sync-test";

        static void Main(string[] args)
        {
            EventTimer.Elapsed += FlushEvents;
            EventTimer.AutoReset = false;
            var watching = StartWatching();
            Console.WriteLine("Ready to watch filesystem.");

            // Setup session options
            SessionOptions sessionOptions = new SessionOptions
            {
                Protocol = Protocol.Sftp,
                HostName = "bfg9000.yandex.ru",
                UserName = "yuraaka",
                SshPrivateKeyPath = @"C:\ssh\id_rsa.ppk",
                SshHostKeyFingerprint = "ssh-ed25519 256 76:7f:2d:3f:3b:63:17:1e:5d:87:74:47:40:cf:33:8f"
            };

            using (Session session = new Session())
            {
                // Connect
                session.ReconnectTime = TimeSpan.MaxValue;
                session.Timeout = TimeSpan.FromSeconds(3);
                while (true)
                {
                    try
                    {
                        session.Open(sessionOptions);
                        Console.WriteLine("Ready to transmit changes.");
                        SyncChanges(session);
                    }
                    catch (SessionRemoteException err)
                    {
                        Console.WriteLine("ERROR: Remote failure: {0}", err);
                        ReopenSession(session);
                        continue;
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("ERROR: Session timeout. Retryng to connect...");
                        ReopenSession(session);
                        continue;
                    }
                }
            }

            Console.ReadKey();
        }

        private static void PrintEvents()
        {
            Console.Write("Events: ");
            foreach (var ev in Events)
            {
                Console.Write(ev.ChangeType.ToString()[0]);
            }

            Console.Write("\n");
        }

        private static void ReopenSession(Session session)
        {
            if (session.Opened)
            {
                session.Close();
            }
        }

        private static string AsRemotePath(Session session, string path)
        {
            return session.TranslateLocalPathToRemote(path, LocalRoot, RemoteRoot);
        }

        private static OperationResultBase RemoteUpdateRecursive(
            Session session,
            string localPath,
            string remotePath,
            bool isDirectory,
            TransferOptions options)
        {
            while (true)
            {
                var result = RemoteUpdate(session, localPath, remotePath, isDirectory, options);
                var localParentDir = Path.GetDirectoryName(localPath);
                var remoteParentDir = AsRemotePath(session, localParentDir);
                if (result == null || result.IsSuccess || session.FileExists(remoteParentDir))
                {
                    return result;
                }

                Console.WriteLine(
                    "ERROR: Cannot create file {0}. Create parent dir {1}",
                    remotePath,
                    remoteParentDir
                );

                var parentResult =
                    RemoteUpdateRecursive(session, localParentDir, remoteParentDir, true, options);

                if (parentResult != null)
                {
                    return parentResult;
                }
            }
        }

        private static OperationResultBase RemoteUpdate(
            Session session,
            string localPath,
            string remotePath,
            bool isDirectory,
            TransferOptions options)
        {
            if (!isDirectory)
            {
                var result = session.PutFiles(localPath, remotePath, false, options);
                Console.WriteLine("Upload {0} to {1}", localPath, remotePath);
                return result;
            }

            if (session.FileExists(remotePath))
            {
                Console.WriteLine("Remove existing directory {0}", remotePath);
                session.RemoveFiles(remotePath).Check();
            }

            session.CreateDirectory(remotePath);
            Console.WriteLine("Create directory {0}", remotePath);
            return null;
        }

        private static OperationResultBase RemoteDelete(Session session, string remotePath)
        {
            if (session.FileExists(remotePath))
            {
                var result = session.RemoveFiles(remotePath);
                Console.WriteLine("Remove {0}", remotePath);
                return result;
            }

            return null;
        }

        private static OperationResultBase RemoteRename(
            Session session,
            string oldRemotePath,
            string localPath,
            string remotePath,
            bool isDirectory,
            TransferOptions options)
        {
            if (isDirectory)
            {
                if (session.FileExists(remotePath))
                {
                    Console.WriteLine("Remove existing directory {0}", remotePath);
                    var result = session.RemoveFiles(remotePath);
                    if (!result.IsSuccess)
                    {
                        return result;
                    }
                }
            }

            if (session.FileExists(oldRemotePath))
            {
                session.MoveFile(oldRemotePath, remotePath);
                Console.WriteLine("Rename from {0} to {1}", oldRemotePath, remotePath);
            }
            else
            {
                Console.WriteLine("Perverting rename to update of {0}", remotePath);
                RemoteUpdateRecursive(session, localPath, remotePath, isDirectory, options);
            }

            return null;
        }

        private static OperationResultBase PropagateChanges(
            Session session,
            TransferOptions options,
            FileSystemEventArgs ev)
        {
            bool isDirectory = IsDirectory(ev.FullPath);
            if (ev.ChangeType == WatcherChangeTypes.Deleted && File.Exists(ev.FullPath))
            {
                ev = new FileSystemEventArgs(WatcherChangeTypes.Changed, LocalRoot, ev.Name);
                Console.WriteLine("Perverting deletion to update {0}", ev.FullPath);
            }
            else if (ev.ChangeType != WatcherChangeTypes.Deleted)
            {
                if (isDirectory && !Directory.Exists(ev.FullPath) || !isDirectory && !File.Exists(ev.FullPath))
                {
                    Console.WriteLine("Skip update nonexisting {0} of file {1}", ev.ChangeType, ev.FullPath);
                    return null;
                }
            }

            string remotePath = AsRemotePath(session, ev.FullPath);
            Console.WriteLine("Propagating event: {0} {1}", ev.ChangeType, ev.FullPath);
            switch (ev.ChangeType)
            {
                case WatcherChangeTypes.Changed:
                case WatcherChangeTypes.Created:
                    return RemoteUpdateRecursive(session, ev.FullPath, remotePath, isDirectory, options);

                case WatcherChangeTypes.Deleted:
                    return RemoteDelete(session, remotePath);

                case WatcherChangeTypes.Renamed:
                    {
                        var rev = (RenamedEventArgs)ev;
                        string oldRemotePath = AsRemotePath(session, rev.OldFullPath);
                        return RemoteRename(session, oldRemotePath, rev.FullPath, remotePath,
                            isDirectory, options);
                    }

                default:
                    return null;
            }
        }

        private static EventPool SnoopChanges()
        {
            EventPool tmpEvents;
            lock (Events)
            {
                if (Events.Count == 0)
                {
                    Console.WriteLine("Waiting for changes...");
                    Monitor.Wait(Events);
                }

                tmpEvents = Events;
                Events = new EventPool();
            }

            return tmpEvents;
        }

        private static FileSystemWatcher StartWatching()
        {
            FileSystemWatcher watcher = new FileSystemWatcher
            {
                Path = LocalRoot,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64*1024
            };

            watcher.Changed += new FileSystemEventHandler(OnChanged);
            watcher.Created += new FileSystemEventHandler(OnChanged);
            watcher.Deleted += new FileSystemEventHandler(OnChanged);
            watcher.Renamed += new RenamedEventHandler(OnChanged);

            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private static EventPool PreprocessEvents(EventPool events)
        {
            // UU* -> U
            // RU  -> U
            // todo do it in stream
            EventPool result = new EventPool();
            for (var i = 0; i < events.Count; ++i)
            {
                if (i == events.Count - 1)
                {
                    result.Add(events[i]);
                    continue;
                }

                var cur = events[i];
                var next = events[i + 1];
                if (cur.FullPath != next.FullPath)
                {
                    result.Add(cur);
                    continue;
                }

                if (cur.ChangeType == next.ChangeType) {
                    Console.WriteLine("Skip same action {0} of {1}", cur.ChangeType, cur.FullPath);
                    continue;
                }

                if (next.ChangeType == WatcherChangeTypes.Deleted)
                {
                    Console.WriteLine("Skip change for next deletion {0}", cur.FullPath);
                    continue;
                }

                if (
                    cur.ChangeType == WatcherChangeTypes.Deleted
                    && (next.ChangeType == WatcherChangeTypes.Changed || next.ChangeType == WatcherChangeTypes.Created)
                )
                {
                    Console.WriteLine("Skip deletion for next change {0}", cur.FullPath);
                    continue;
                }

                result.Add(cur);
            }

            return result;
        }

        private static void SyncChanges(Session session)
        {
            TransferOptions transferOptions = new TransferOptions
            {
                TransferMode = TransferMode.Binary,
                ResumeSupport = new TransferResumeSupport
                {
                    State = TransferResumeSupportState.Smart,
                    Threshold = 1024
                }
            };

            Exception unrecoverableError = null;
            EventPool writeBackEvents = new EventPool();
            while (true)
            {
                var events = SnoopChanges();
                events = PreprocessEvents(events);
                foreach (var ev in events)
                {
                    if (unrecoverableError != null)
                    {
                        writeBackEvents.Add(ev);
                        continue;
                    }

                    OperationResultBase result = null;
                    try
                    {
                        result = PropagateChanges(session, transferOptions, ev);
                    }
                    catch (SessionRemoteException err)
                    {
                        Console.WriteLine("ERROR: {0}", err);
                    }
                    catch (Exception err)
                    {
                        Console.WriteLine("FAILURE: {0}. \nContinue collecting events...", err);
                        unrecoverableError = err;
                        writeBackEvents.Add(ev);
                        continue;
                    }

                    if (result != null && !result.IsSuccess)
                    {
                        foreach (var fail in result.Failures)
                        {
                            Console.WriteLine("ERROR: {0}", fail);
                        }
                    }
                }

                if (unrecoverableError != null)
                {
                    lock (Events)
                    {
                        foreach (var ev in writeBackEvents)
                        {
                            Events.Add(ev);
                        }
                    }

                    throw unrecoverableError;
                }
            }
        }

        private static FileAttributes? SafeGetAttrs(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static bool IsDirectory(string path)
        {
            var attr = SafeGetAttrs(path);
            return attr.HasValue && (attr.Value & FileAttributes.Directory) == FileAttributes.Directory;
        }

        private static void OnChanged(object source, FileSystemEventArgs e)
        {
            Console.WriteLine("IN: Timer reset: {0}, {1}", e.ChangeType, e.FullPath);
            EventTimer.Enabled = false;
            EventTimer.Enabled = true;

            if (e.FullPath.Contains("~"))
            {
                Console.WriteLine("IN: Skip tilda: {0}", e.FullPath);
                PrintEvents();
                return;
            }

            if (e.ChangeType == WatcherChangeTypes.Renamed)
            {
                RenamedEventArgs re = (RenamedEventArgs)e;
                if (re.OldFullPath.Contains("~"))
                {
                    e = new FileSystemEventArgs(WatcherChangeTypes.Changed, LocalRoot, re.Name);
                    Console.WriteLine("IN: Refactor rename to update: {0}", e.FullPath);
                }
            }

            if (e.ChangeType == WatcherChangeTypes.Changed)
            {
                if (IsDirectory(e.FullPath))
                {
                    Console.WriteLine("IN: Skip directory update: {0}", e.FullPath);
                    PrintEvents();
                    return;
                }
            }


            lock (Events)
            {
                if (Events.Count == 0)
                {
                    Console.WriteLine("IN: Add first event: {0}", e.FullPath);
                    Events.Add(e);
                    PrintEvents();
                    return;
                }

                var last = Events[Events.Count - 1];
                if (last.FullPath != e.FullPath)
                {
                    Console.WriteLine("IN: Add different events: {0} => {1}", last.FullPath, e.FullPath);
                    Events.Add(e);
                    PrintEvents();
                    return;
                }

                Events.Add(e);
                bool ruleApply = false;
                var curIdx = Events.Count - 1;
                var precIdx = Events.Count - 2;
                while (Events[precIdx].FullPath == Events[curIdx].FullPath)
                {
                    var prec = Events[precIdx];
                    var cur = Events[curIdx];

                    if (prec.ChangeType == cur.ChangeType)
                    {
                        Console.WriteLine("IN: Skip duplicate: {0} => {1}", last.FullPath, e.FullPath);
                    }
                    else if (prec.ChangeType == WatcherChangeTypes.Deleted && cur.ChangeType != WatcherChangeTypes.Deleted)
                    {
                        Console.WriteLine("IN: Rewrite deletion for update: {0} => {1}", prec.FullPath, cur.FullPath);
                    }
                    else if (
                        (prec.ChangeType == WatcherChangeTypes.Changed || prec.ChangeType == WatcherChangeTypes.Created)
                        && cur.ChangeType == WatcherChangeTypes.Renamed
                    )
                    {
                        Console.WriteLine("IN: Rewrite rename for update: {0} => {1}", prec.FullPath, cur.FullPath);
                    }
                    else if (
                        (prec.ChangeType == WatcherChangeTypes.Changed || prec.ChangeType == WatcherChangeTypes.Created)
                        && (cur.ChangeType == WatcherChangeTypes.Changed || cur.ChangeType == WatcherChangeTypes.Created))
                    {
                        Console.WriteLine("IN: Rewrite create for update: {0} => {1}", prec.FullPath, cur.FullPath);
                    }
                    else if (prec.ChangeType != WatcherChangeTypes.Deleted && cur.ChangeType == WatcherChangeTypes.Deleted)
                    {
                        Console.WriteLine("IN: Rewrite update for deletion: {0} => {1}", prec.FullPath, cur.FullPath);
                    }
                    else
                    {
                        break;
                    }

                    ruleApply = true;
                    Events[precIdx] = Events[curIdx];
                    Events.RemoveAt(curIdx);
                    PrintEvents();
                    if (precIdx == 0)
                    {
                        break;
                    }

                    --precIdx;
                    --curIdx;
                }

                if (!ruleApply)
                {
                    Console.WriteLine("IN: Added, no rule applied: {0}", e.FullPath);
                    PrintEvents();
                }
            }
        }

        private static void FlushEvents(Object source, System.Timers.ElapsedEventArgs e)
        {
            Console.WriteLine("Timer signals");
            lock (Events)
            {
                if (Events.Count > 0)
                {
                    Monitor.Pulse(Events);
                }
            }
        }
    }
}
