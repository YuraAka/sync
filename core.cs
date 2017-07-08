using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Security;
using WinSCP;

namespace Core {
    using EventPool = List<FileSystemEventArgs>;
    using Timer = System.Timers.Timer;
    using Changes = WatcherChangeTypes;
    using TransmitResult = OperationResultBase;
    using System.Threading.Tasks;

    public delegate bool Predicate();

    class Utils {
        public static bool IsDirectory(string path) {
            var attrs = TryAttrs(path);
            return attrs.HasValue
                && (attrs.Value & FileAttributes.Directory) == FileAttributes.Directory;
        }

        private static FileAttributes? TryAttrs(string path) {
            try {
                return File.GetAttributes(path);
            } catch (DirectoryNotFoundException) {
                return null;
            } catch (FileNotFoundException) {
                return null;
            }
        }
    }

    interface ILogger {
        void Debug(string format, params object[] arg);

        void Info(string format, params object[] arg);

        void Error(string format, params object[] arg);
    };

    class CompoundLogger : ILogger {
        public ILogger[] Logs { get; set; }

        public virtual void Debug(string format, params object[] arg) {
            foreach (var log in Logs) {
                log.Debug(format, arg);
            }
        }

        public virtual void Info(string format, params object[] arg) {
            foreach (var log in Logs) {
                log.Info(format, arg);
            }
        }

        public virtual void Error(string format, params object[] arg) {
            foreach (var log in Logs) {
                log.Error(format, arg);
            }
        }
    }

    class EventLogger : ILogger {
        private EventLog Log;

        public EventLogger() {
            try {
                if (!EventLog.SourceExists("YSync")) {
                    EventLog.CreateEventSource("YSync", "YSync");
                }

                Log = new EventLog("YSync") {
                    Source = "YSync"
                };
            } catch (SecurityException) {
                Console.WriteLine("ERR: Cannot create event log. Please, run once with administrative priviledges.");
            }
        }

        public virtual void Debug(string format, params object[] arg) {
            if (Log != null) {
                Log.WriteEntry(String.Format(format, arg), EventLogEntryType.Information, 300, 3);
            }
        }

        public virtual void Info(string format, params object[] arg) {
            if (Log != null) {
                Log.WriteEntry(String.Format(format, arg), EventLogEntryType.Information, 200, 2);
            }
        }

        public virtual void Error(string format, params object[] arg) {
            if (Log != null) {
                Log.WriteEntry(String.Format(format, arg), EventLogEntryType.Error, 100, 1);
            }
        }
    }

    class ConsoleLogger : ILogger {
        public int Verbosity { get; set; } = 0;

        public virtual void Debug(string format, params object[] arg) {
            if (Verbosity >= 2) {
                Console.WriteLine("DBG: " + format, arg);
            }
        }

        public virtual void Info(string format, params object[] arg) {
            if (Verbosity >= 1) {
                Console.WriteLine("INF: " + format, arg);
            }
        }

        public virtual void Error(string format, params object[] arg) {
            Console.WriteLine("ERR: " + format, arg);
        }
    };

    class ChangesMonitor {
        private EventPool Events = new EventPool();
        private Timer Quantifier;
        private ILogger Log;
        private FileSystemWatcher Watcher;
        private Predicate IsCancel;
        private bool FirstChangeInSeries = false;

        public string SourcePath { get; set; }
        public HashSet<string> Excludes { get; set; }
        public event Action OnWait = new Action(() => {});
        public event Action OnChangesCollect = new Action(() => { });

        public ChangesMonitor(ILogger log) {
            Log = log;
            Quantifier = new Timer() {
                AutoReset = true,
                Interval = 100
            };

            Quantifier.Elapsed += Flush;
        }

        public void TurnOn() {
            IsCancel = null;
            Watcher = CreateWatcher();
            Log.Info("Ready to monitor filesystem.");
        }

        public EventPool Wait(Predicate cancelPoller) {
            IsCancel = cancelPoller;
            EventPool result;
            lock (Events) {
                if (Events.Count == 0) {
                    Log.Info("Waiting for changes...");
                    OnWait();
                    Monitor.Wait(Events);
                    FirstChangeInSeries = true;
                }

                if (IsCancel()) {
                    return null;
                }

                result = Events;
                Events = new EventPool();
            }

            return result;
        }

        private FileSystemWatcher CreateWatcher() {
            var watcher = new FileSystemWatcher {
                Path = SourcePath,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };

            watcher.Changed += new FileSystemEventHandler(OnChanged);
            watcher.Created += new FileSystemEventHandler(OnChanged);
            watcher.Deleted += new FileSystemEventHandler(OnChanged);
            watcher.Renamed += new RenamedEventHandler(OnChanged);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void Flush(Object source, System.Timers.ElapsedEventArgs ev) {
            lock (Events) {
                if (Events.Count > 0 || IsCancel()) {
                    Monitor.Pulse(Events);
                }
            }
        }

        private void OnChanged(object source, FileSystemEventArgs ev) {
            Quantifier.Enabled = false;
            Quantifier.Enabled = true;

            if (TrySkip(ev)) {
                return;
            }

            if (FirstChangeInSeries) {
                OnChangesCollect();
                FirstChangeInSeries = false;
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
            if (ev.ChangeType == Changes.Renamed) {
                RenamedEventArgs re = (RenamedEventArgs)ev;
                if (re.OldFullPath.Contains("~")) {
                    ev = new FileSystemEventArgs(Changes.Changed, SourcePath, re.Name);
                    Log.Debug("IN: Refactor rename to update: {0}", ev.FullPath);
                }
            }

            return ev;
        }

        private bool TrySkip(FileSystemEventArgs ev) {
            if (ev.FullPath.Contains("~")) {
                Log.Debug("IN: Skip tilda: {0}", ev.FullPath);
                return true;
            }

            /// todo regexp
            foreach (var part in ev.FullPath.Split(Path.DirectorySeparatorChar)) {
                if (Excludes.Contains(part)) {
                    Log.Debug("IN: Skip exclusion: {0}", ev.FullPath);
                    return true;
                }
            }

            if (ev.ChangeType == Changes.Changed) {
                if (Utils.IsDirectory(ev.FullPath)) {
                    Log.Debug("IN: Skip directory update: {0}", ev.FullPath);
                    return true;
                }
            }

            return false;
        }

        private bool TryCommitNew(FileSystemEventArgs ev) {
            if (Events.Count == 0) {
                Log.Debug("IN: Add first event: {0}", ev.FullPath);
                LogEventsNoLock();
                return true;
            }

            var last = Events[Events.Count - 1];
            if (last.FullPath != ev.FullPath) {
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
            while (Events.Count > 1 && Events[prevIdx].FullPath == Events[curIdx].FullPath) {
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

        private void LogEventsNoLock() {
            var stream = new StringWriter();
            stream.Write("Events: ");
            foreach (var ev in Events) {
                stream.Write(ev.ChangeType.ToString()[0]);
            }

            stream.Write("\n");
            Log.Debug(stream.ToString());
        }
    };

    class ChangesTransmitter {
        private Session Session = new Session();
        private ILogger Log;
        private EventPool LostLifeChanges = new EventPool();
        private string SourcePath { set; get; }
        private Predicate IsCancel;

        public string Host { set; get; }
        public string User { set; get; }
        public string PrivateKey { set; get; }
        public string Passphrase { set; get; }
        public string DestinationPath { set; get; }
        public bool DryRun { set; get; }
        public TimeSpan Timeout { set; get; } = TimeSpan.FromSeconds(30);
        public event Action OnSessionClose = new Action(() => { });
        public event Action OnSessionOpen = new Action(() => { });
        public event Action OnChangesStart = new Action(() => { });
        public event Action OnChangesFinish = new Action(() => { });

        public ChangesTransmitter(ILogger log) {
            Log = log;
            Session.ReconnectTime = TimeSpan.MaxValue;
            UnpackWinSCP();
        }

        public void Stop() {
            Session.Abort();
        }

        public void WaitChanges(ChangesMonitor source, Predicate cancelPoller) {
            SourcePath = source.SourcePath;
            var sessionOptions = CreateSessionOptions();
            var transferOptions = CreateTransferOptions();
            Session.Timeout = Timeout;
            IsCancel = cancelPoller;

            while (!IsCancel()) {
                try {
                    OpenSession(sessionOptions);
                    while (!IsCancel()) {
                        var changes = RetrieveChanges(source);
                        OnChangesStart();
                        if (changes == null) {
                            return;
                        }

                        TransferAll(changes, transferOptions);
                        OnChangesFinish();
                    }
                } catch (SessionRemoteException err) {
                    Log.Error("Remote failure: {0}", err);
                } catch (TimeoutException) {
                    Log.Error("Session timeout. Retryng to connect...");
                } catch (InvalidOperationException err) {
                    Log.Error("Remote failure: {0}", err);
                } catch (SessionLocalException err) {
                    Log.Error("Local failure: {0}", err);
                } catch (Exception err) {
                    Log.Error("UNKNOWN EXCEPTION: {0}. Yury, you MUST fix it!", err);

                    if (LostLifeChanges.Count > 0) {
                        var first = LostLifeChanges[0];
                        Log.Debug("Skip problem change: {0} {1}", first.ChangeType, first.FullPath);
                        LostLifeChanges.RemoveAt(0);
                    }

                    Thread.Sleep(1000);
                } finally {
                    CloseSession();
                }
            }
        }

        private void OpenSession(SessionOptions options) {
            if (DryRun) {
                Log.Info("Fake session open");
                return;
            }

            if (options.SshHostKeyFingerprint == null) {
                options.SshHostKeyFingerprint = Session.ScanFingerprint(options);
            }

            if (!Session.Opened) {
                Session.Open(options);
                OnSessionOpen();
                Log.Info("Ready to transmit changes.");
            }
        }

        private void UnpackWinSCP() {
            string executableName = "WinSCP.exe";
            string executablePath = Path.Combine(Directory.GetCurrentDirectory(), executableName);
            if (File.Exists(executablePath)) {
                Log.Debug("Using existing winscp {0}", executablePath);
                return;
            }

            try {
                Assembly executingAssembly = Assembly.GetExecutingAssembly();
                /// todo: potential bug here: we must use namespace name, not assembly name
                string resourceName = executingAssembly.GetName().Name + "." + "WinSCP.exe";
                using (Stream resource = executingAssembly.GetManifestResourceStream(resourceName))
                using (Stream file = new FileStream(executablePath, FileMode.Create, FileAccess.Write)) {
                    resource.CopyTo(file);
                }
            } catch (Exception) {
                File.Delete(executablePath);
                throw;
            }

            Log.Debug("Unpack winscp {0}", executablePath);
        }

        private SessionOptions CreateSessionOptions() {
            return new SessionOptions {
                HostName = Host,
                UserName = User,
                SshPrivateKeyPath = PrivateKey,
                PrivateKeyPassphrase = Passphrase,
                Protocol = Protocol.Scp
            };
        }

        private TransferOptions CreateTransferOptions() {
            return new TransferOptions {
                TransferMode = TransferMode.Binary,
                ResumeSupport = new TransferResumeSupport {
                    State = TransferResumeSupportState.Smart,
                    Threshold = 4 * 1024
                }
            };
        }

        private void TransferAll(EventPool changes, TransferOptions options) {
            Exception lostLifeError = null;
            if (changes.Count > 100) {
                Log.Info("To many changes. Need to fallback to batch method");
                // TODO: get all changes, convert to tree and separate several biggest subtrees,
                // then sync only roots with rsync
            }

            foreach (var change in changes) {
                if (IsCancel()) {
                    return;
                }

                if (lostLifeError != null) {
                    LostLifeChanges.Add(change);
                    continue;
                }

                try {
                    TransferOne(change, options);
                } catch (SessionRemoteException err) {
                    Log.Error("Skip change {0} {1} due to {2}", change.ChangeType, change.FullPath, err);
                } catch (Exception err) {
                    Log.Error("Emergency changes save started due to error: {0}", err);
                    lostLifeError = err;
                    LostLifeChanges.Add(change);
                }
            }

            if (lostLifeError != null) {
                throw lostLifeError;
            }
        }

        private void TransferOne(FileSystemEventArgs change, TransferOptions options) {
            if (DryRun) {
                Log.Info("Fake transfer " + change.FullPath);
                return;
            }

            change = Transform(change);
            if (change == null) {
                return;
            }

            Log.Debug("Transfer change {0} {1}", change.ChangeType, change.FullPath);
            switch (change.ChangeType) {
                case Changes.Changed:
                case Changes.Created:
                    Update(change.FullPath, options);
                    break;

                case Changes.Deleted:
                    Delete(change.FullPath);
                    break;

                case Changes.Renamed:
                    Rename(GetOldPath(change), change.FullPath);
                    break;
            }
        }

        private string GetOldPath(FileSystemEventArgs change) {
            if (change.ChangeType != Changes.Renamed) {
                throw new InvalidOperationException("Change is not a rename change");
            }

            var rename = (RenamedEventArgs)change;
            return rename.OldFullPath;
        }

        private void CreateDirWithParents(string dirPath) {
            string relativePath = dirPath.Substring(SourcePath.Length);
            var separator = new[] { Path.DirectorySeparatorChar };
            var subdirs = relativePath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            string readyPath = string.Copy(SourcePath);
            foreach (var subdir in subdirs) {
                readyPath = Path.Combine(readyPath, subdir) + Path.DirectorySeparatorChar;
                var remotePath = AsRemotePath(readyPath);
                if (!Session.FileExists(remotePath)) {
                    Session.CreateDirectory(remotePath);
                }
            }
        }

        private void Update(string path, TransferOptions options) {
            bool isDir = Utils.IsDirectory(path);
            try {
                if (isDir) {
                    UpdateDirectory(path);
                } else {
                    UpdateFile(path, options);
                }
            } catch (SessionRemoteException err) {
                var remotePath = AsRemotePath(path);
                Log.Debug("Update {0} failed: {1}", remotePath, err);
                var localParentDir = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
                var remoteParentDir = AsRemotePath(localParentDir);
                if (Session.FileExists(remoteParentDir)) {
                    throw;
                }

                CreateDirWithParents(localParentDir);
                Log.Debug("Retrying after parents creation");
                Update(path, options);
            }
        }

        private void UpdateFile(string localPath, TransferOptions options) {
            var remotePath = AsRemotePath(localPath);
            Session.PutFiles(localPath, remotePath, false, options).Check();
            Log.Info("Upload {0} to {1}", localPath, remotePath);
        }

        private void UpdateDirectory(string localPath) {
            var remotePath = AsRemotePath(localPath);
            if (Session.FileExists(remotePath)) {
                Log.Debug("Remove existing directory {0}", remotePath);
                Session.RemoveFiles(remotePath).Check();
            }

            Session.CreateDirectory(remotePath);
            Log.Info("Create directory {0}", remotePath);
        }

        private void Delete(string localPath) {
            var remotePath = AsRemotePath(localPath);
            if (Session.FileExists(remotePath)) {
                Session.RemoveFiles(remotePath).Check();
                Log.Info("Remove {0}", remotePath);
            }
        }

        private void Rename(string fromPath, string toPath) {
            if (Utils.IsDirectory(toPath)) {
                Delete(toPath);
            }

            var fromRemotePath = AsRemotePath(fromPath);
            var toRemotePath = AsRemotePath(toPath);
            Session.MoveFile(fromRemotePath, toRemotePath);
            Log.Info("Rename from {0} to {1}", fromRemotePath, toRemotePath);
        }

        private void LogResult(TransmitResult result) {
            if (result.IsSuccess) {
                return;
            }

            StringWriter msg = new StringWriter();
            msg.Write("Operation has failed. Suberrors: \n");
            foreach (var fail in result.Failures) {
                msg.Write("* Error {0}\n", fail);
            }

            Log.Error(msg.ToString());
        }

        private FileSystemEventArgs Transform(FileSystemEventArgs change) {
            bool exist = File.Exists(change.FullPath) || Directory.Exists(change.FullPath);
            if (change.ChangeType == Changes.Deleted && exist) {
                Log.Debug("Transform deletion to update {0} due to file exist", change.FullPath);
                return new FileSystemEventArgs(Changes.Changed, SourcePath, change.Name);
            }

            if (change.ChangeType == Changes.Deleted) {
                return change;
            }

            if (!exist) {
                Log.Debug("Skip update nonexisting {0} of file {1}", change.ChangeType, change.FullPath);
                return null;
            }

            if (change.ChangeType != Changes.Renamed) {
                return change;
            }

            var rename = (RenamedEventArgs)change;
            var remoteOldPath = AsRemotePath(rename.OldFullPath);
            if (!Session.FileExists(remoteOldPath)) {
                Log.Debug("Transform rename to update {0} due to file does not exist", remoteOldPath);
                return new FileSystemEventArgs(Changes.Changed, SourcePath, change.Name);
            }

            return change;
        }

        private EventPool RetrieveChanges(ChangesMonitor source) {
            if (LostLifeChanges.Count == 0) {
                return source.Wait(IsCancel);
            }

            // we failed last time and there are unprocessed changes
            var changes = new EventPool(LostLifeChanges);
            LostLifeChanges.Clear();
            return changes;
        }

        private void CloseSession() {
            if (DryRun) {
                Log.Info("Fake session close");
                return;
            }

            if (Session.Opened) {
                OnSessionClose();
                Session.Close();
            }
        }

        private string AsRemotePath(string path) {
            return Session.TranslateLocalPathToRemote(path, SourcePath, DestinationPath);
        }
    }

    class InteractiveFileChangesSync {
        public string User { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string Host { get; set; }
        public string PrivateKey { get; set; }
        public HashSet<string> Excludes { get; set; }

        public event Action OnChangesProcessed = new Action(() => { });
        public event Action OnChangesWait = new Action(() => { });
        public event Action OnStop = new Action(() => { });

        public void Start(Predicate cancelPoller) {
            OnChangesWait();
            ILogger logger = new EventLogger();

            /// add delegates
            ChangesMonitor monitor = new ChangesMonitor(logger) {
                SourcePath = SourcePath,
                Excludes = Excludes,
            };

            ChangesTransmitter transmitter = new ChangesTransmitter(logger) {
                Host = Host,
                User = User,
                PrivateKey = PrivateKey,
                DestinationPath = DestinationPath,
            };

            monitor.OnChangesCollect += OnChangesProcessed;
            monitor.OnWait += OnChangesWait;

            monitor.TurnOn();

            var cancelListen = new Task(new Action(() => {
                while (!cancelPoller()) {
                    Thread.Sleep(1000);
                }

                //monitor.TurnOff();
                transmitter.Stop();
            }));

            cancelListen.Start();

            transmitter.WaitChanges(monitor, cancelPoller);
            OnStop();
            logger.Info("Stopping...");
        }
    }

    class SimpleFileChangesSync {
        public string User { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string Host { get; set; }
        public string PrivateKey { get; set; }
        public int Verbosity { get; set; }
        public HashSet<string> Excludes { get; set; }
        public bool DryRun { get; set; }

        public void Run() {
            ILogger logger = new CompoundLogger() {
                Logs = new ILogger[] {
                    new EventLogger(),
                    new ConsoleLogger() {
                        Verbosity = Verbosity
                    }
                }
            };

            ChangesMonitor monitor = new ChangesMonitor(logger) {
                SourcePath = SourcePath,
                Excludes = Excludes
            };

            ChangesTransmitter transmitter = new ChangesTransmitter(logger) {
                Host = Host,
                User = User,
                PrivateKey = PrivateKey,
                DestinationPath = DestinationPath,
                DryRun = DryRun,
            };

            monitor.TurnOn();
            transmitter.WaitChanges(monitor, () => false);
        }
    }
}