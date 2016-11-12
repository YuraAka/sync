using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using WinSCP;

/// TODO
/// event monitor vs event transmitter
/// exceptions are cheap?
/// lock on ref object
/// exception handling
/// event log
/// user params from registry
/// detect and run batch load
/// gui
///
namespace sync
{
    using EventPool = List<FileSystemEventArgs>;
    using Timer = System.Timers.Timer;
    using Changes = WatcherChangeTypes;

    class Utils {
        public static bool IsDirectory(string path)
        {
            var attrs = TryAttrs(path);
            return attrs.HasValue
                && (attrs.Value & FileAttributes.Directory) == FileAttributes.Directory;
        }

        private static FileAttributes? TryAttrs(string path) {
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
    }

    class Logger {
        public void Debug(string format, params object[] arg) {
            Console.WriteLine(format, arg);
        }

        public void Info(string format, params object[] arg)
        {
            Console.WriteLine(format, arg);
        }
    };

    class EventMonitor {
        private EventPool Events = new EventPool();
        private Timer Quantifier;
        private FileSystemWatcher FileMonitor;
        private Logger Log;
        private string Path;

        public EventMonitor(string path, Logger log) {
            Path = path;
            Log = log;
            Quantifier = new Timer() {
                AutoReset = false,
                Interval = 100
            };

            Quantifier.Elapsed += Flush;

            FileMonitor = new FileSystemWatcher
            {
                Path = path,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };

            FileMonitor.Changed += new FileSystemEventHandler(OnChanged);
            FileMonitor.Created += new FileSystemEventHandler(OnChanged);
            FileMonitor.Deleted += new FileSystemEventHandler(OnChanged);
            FileMonitor.Renamed += new RenamedEventHandler(OnChanged);
            FileMonitor.EnableRaisingEvents = true;
            Log.Info("Ready to monitor filesystem.");
        }

        public EventPool Wait() {
            EventPool result;
            lock (Events) {
                if (Events.Count == 0)
                {
                    Log.Info("Waiting for changes...");
                    Monitor.Wait(Events);
                }

                result = Events;
                Events = new EventPool();
            }

            return result;
        }

        private void Flush(Object source, System.Timers.ElapsedEventArgs ev) {
            Log.Debug("Timer signals");
            lock (Events)
            {
                if (Events.Count > 0)
                {
                    Monitor.Pulse(Events);
                }
            }
        }

        private void OnChanged(object source, FileSystemEventArgs ev) {
            Log.Debug("IN: Timer reset: {0}, {1}", ev.ChangeType, ev.FullPath);
            Quantifier.Enabled = false;
            Quantifier.Enabled = true;

            if (TrySkip(ev)) {
                return;
            }

            ev = Transform(ev);
            lock (Events) {
                Events.Add(ev);
                if (TryCommitNew(ev)) {
                    return;
                }

                if (!TryReduce(ev)) {
                    Log.Debug("IN: Added, no rule applied: {0}", ev.FullPath);
                    LogEventsNoLock();
                }
            }
        }

        private FileSystemEventArgs Transform(FileSystemEventArgs ev) {
            if (ev.ChangeType == Changes.Renamed)
            {
                RenamedEventArgs re = (RenamedEventArgs)ev;
                if (re.OldFullPath.Contains("~"))
                {
                    ev = new FileSystemEventArgs(Changes.Changed, Path, re.Name);
                    Log.Debug("IN: Refactor rename to update: {0}", ev.FullPath);
                }
            }

            return ev;
        }

        private bool TrySkip(FileSystemEventArgs ev) {
            if (ev.FullPath.Contains("~"))
            {
                Log.Debug("IN: Skip tilda: {0}", ev.FullPath);
                return true;
            }

            /// todo regexp
            if (ev.FullPath.Contains(".svn"))
            {
                Log.Debug("IN: Skip exclusion: {0}", ev.FullPath);
                return true;
            }

            if (ev.ChangeType == Changes.Changed)
            {
                if (Utils.IsDirectory(ev.FullPath))
                {
                    Log.Debug("IN: Skip directory update: {0}", ev.FullPath);
                    return true;
                }
            }

            return false;
        }

        private bool TryCommitNew(FileSystemEventArgs ev) {
            if (Events.Count == 0)
            {
                Log.Debug("IN: Add first event: {0}", ev.FullPath);
                LogEventsNoLock();
                return true;
            }

            var last = Events[Events.Count - 1];
            if (last.FullPath != ev.FullPath)
            {
                Log.Debug("IN: Add different events: {0} => {1}", last.FullPath, ev.FullPath);
                LogEventsNoLock();
                return true;
            }

            return false;
        }

        private bool TryReduce(FileSystemEventArgs ev) {
            bool applied = false;
            var curIdx = Events.Count - 1;
            var prevIdx = Events.Count - 2;
            while (Events.Count > 1 && Events[prevIdx].FullPath == Events[curIdx].FullPath)
            {
                var prev = Events[prevIdx];
                var cur = Events[curIdx];
                if (prev.ChangeType == cur.ChangeType) {
                    Log.Debug("IN: Skip duplicate: {0} => {1}", prev.FullPath, cur.FullPath);
                } else if (prev.ChangeType == Changes.Deleted && cur.ChangeType != Changes.Deleted) {
                    Log.Debug("IN: Rewrite deletion for update: {0} => {1}", prev.FullPath, cur.FullPath);
                } else if ((prev.ChangeType & (Changes.Changed | Changes.Created)) != 0) {
                    if (cur.ChangeType == Changes.Renamed) {
                        Log.Debug("IN: Rewrite rename for update: {0} => {1}", prev.FullPath, cur.FullPath);
                    } else if ((cur.ChangeType & (Changes.Changed | Changes.Created)) != 0) {
                        Log.Debug("IN: Rewrite create for update: {0} => {1}", prev.FullPath, cur.FullPath);
                    }
                } else if (prev.ChangeType != Changes.Deleted && cur.ChangeType == Changes.Deleted) {
                    Log.Debug("IN: Rewrite update for deletion: {0} => {1}", prev.FullPath, cur.FullPath);
                } else {
                    return false;
                }

                applied = true;
                Events[prevIdx] = Events[curIdx];
                Events.RemoveAt(curIdx);
                LogEventsNoLock();
                if (prevIdx == 0) {
                    return true;
                }

                --prevIdx;
                --curIdx;
            }

            return applied;
        }

        private void LogEventsNoLock()
        {
            var stream = new StringWriter();
            stream.Write("Events: ");
            foreach (var ev in Events)
            {
                stream.Write(ev.ChangeType.ToString()[0]);
            }

            stream.Write("\n");
            Log.Debug(stream.ToString());
        }
    };

    class Program
    {
        private static EventPool Events
            = new EventPool();

        private static Timer EventTimer = new Timer(100);

        private static string RemoteRoot = "/home/yuraaka/blue/"; //"/home/yuraaka/sync-test/"; //
        private static string LocalRoot = @"C:\dev\blue\arc\"; //@"C:\sync-test";

        static void Main(string[] args)
        {
            Logger logger = new Logger();
            EventMonitor monitor = new EventMonitor(LocalRoot, logger);

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
                session.Timeout = TimeSpan.FromSeconds(5);
                while (true)
                {
                    try
                    {
                        session.Open(sessionOptions);
                        Console.WriteLine("Ready to transmit changes.");
                        SyncChanges(session, monitor);
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
                var localParentDir = Path.GetDirectoryName(localPath) + Path.DirectorySeparatorChar;
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

                if (parentResult == null || parentResult.IsSuccess)
                {
                    continue;
                }

                return parentResult;
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
            bool isDirectory = Utils.IsDirectory(ev.FullPath);
            if (ev.ChangeType == Changes.Deleted && File.Exists(ev.FullPath))
            {
                ev = new FileSystemEventArgs(Changes.Changed, LocalRoot, ev.Name);
                Console.WriteLine("Perverting deletion to update {0}", ev.FullPath);
            }
            else if (ev.ChangeType != Changes.Deleted)
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
                case Changes.Changed:
                case Changes.Created:
                    return RemoteUpdateRecursive(session, ev.FullPath, remotePath, isDirectory, options);

                case Changes.Deleted:
                    return RemoteDelete(session, remotePath);

                case Changes.Renamed:
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

                if (next.ChangeType == Changes.Deleted)
                {
                    Console.WriteLine("Skip change for next deletion {0}", cur.FullPath);
                    continue;
                }

                if (
                    cur.ChangeType == Changes.Deleted
                    && (next.ChangeType == Changes.Changed || next.ChangeType == Changes.Created)
                )
                {
                    Console.WriteLine("Skip deletion for next change {0}", cur.FullPath);
                    continue;
                }

                result.Add(cur);
            }

            return result;
        }

        private static void SyncChanges(Session session, EventMonitor monitor)
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
                var events = monitor.Wait();
                events = PreprocessEvents(events);
                Console.WriteLine("Propagating {0} events", events.Count);
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
    }
}
