using System;
using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms.Design;
using System.Drawing.Design;
using System.Threading;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.Specialized;
using System.Linq;

/// <summary>
/// TODO
/// - bad font
/// - fill settings
/// - change icon color
/// - tabstops
/// - icons
/// - мгновенный стоп: прерывать загрузку файла
/// - progress
/// - executable attr transmit
/// </summary>
namespace gui {
    public class CsvConverter : TypeConverter {
        // Overrides the ConvertTo method of TypeConverter.
        public override object ConvertTo(
            ITypeDescriptorContext context,
            CultureInfo culture,
            object value,
            Type destinationType
        ) {
            var v = value as StringCollection;
            if (destinationType == typeof(string)) {
                return String.Join("; ", v.Cast<String>().ToList());
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }

    public class SettingsFrontend {
        private Settings Backend = new Settings();

        public void Save() {
            Backend.Save();
        }

        public string User {
            get { return Backend.User; }
            set { Backend.User = value; }
        }

        [DisplayName("Private key path")]
        [EditorAttribute(typeof(FileNameEditor), typeof(UITypeEditor))]
        public string PrivateKey {
            get { return Backend.PrivateKey; }
            set { Backend.PrivateKey = value; }
        }

        [DisplayName("Local directory path")]
        [EditorAttribute(typeof(FolderNameEditor), typeof(UITypeEditor))]
        public string SourcePath {
            get { return Backend.SourcePath; }
            set { Backend.SourcePath = value;  }
        }
        public string Host {
            get { return Backend.Host; }
            set { Backend.Host = value; }
        }

        [DisplayName("Remote directory path")]
        public string DestinationPath {
            get { return Backend.DestinationPath; }
            set { Backend.DestinationPath = value; }
        }

        [DisplayName("Excludes")]
        [Editor(@"System.Windows.Forms.Design.StringCollectionEditor," +
        "System.Design, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
        typeof(System.Drawing.Design.UITypeEditor))]
        [TypeConverter(typeof(CsvConverter))]
        public StringCollection Excludes {
            get {
                if (Backend.Excludes == null) {
                    Backend.Excludes = new StringCollection();
                    string[] defaults = { ".svn", ".git", ".vs" };
                    Backend.Excludes.AddRange(defaults);
                }

                return Backend.Excludes;
            }
        }
    };

    public class CustomApplicationContext : ApplicationContext {
        private readonly Icon StartedIcon = new Icon("online.ico");
        private readonly Icon StoppedIcon = new Icon("offline.ico");
        private readonly Icon InWorkIcon = new Icon("onwork.ico");
        private ToolStripItem StartItem;
        private ToolStripItem StopItem;
        private BackgroundWorker SyncWorker;

        private SettingsForm SettingsForm;
        private SettingsFrontend Settings = new SettingsFrontend();
        private System.ComponentModel.IContainer Components;
        private NotifyIcon TrayIcon;

        public CustomApplicationContext() {
            InitializeContext();
        }

        private ToolStripMenuItem CreateItem(string text, EventHandler onEvent, bool enabled = true) {
            var item = new ToolStripMenuItem(text) {
                Enabled = enabled
            };

            if (onEvent != null) {
                item.Click += onEvent;
            }

            return item;
        }

        private void OnCloseSettings(object sender, EventArgs e) {
            Settings.Save();
            SettingsForm = null;
        }

        private void OnSettings(object sender, EventArgs e) {
            if (SettingsForm == null) {
                SettingsForm = new SettingsForm(Settings);
                SettingsForm.Closed += OnCloseSettings;
                SettingsForm.Show();
            } else {
                SettingsForm.Activate();
            }
        }

        private void OnStart(object sender, EventArgs e) {
            SyncWorker.RunWorkerAsync();
        }

        private void OnStop(object sender, EventArgs e) {
            SyncWorker.CancelAsync();
        }

        private void OnExit(object sender, EventArgs e) {
            ExitThread();
        }

        private void OnContextMenu(object sender, System.ComponentModel.CancelEventArgs e) {
            e.Cancel = false;

            TrayIcon.ContextMenuStrip.Items.Clear();
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Settings", OnSettings));
            TrayIcon.ContextMenuStrip.Items.Add(StartItem);
            TrayIcon.ContextMenuStrip.Items.Add(StopItem);
            TrayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Exit", OnExit));
        }

        private void InitializeContext() {
            Components = new System.ComponentModel.Container();
            TrayIcon = new NotifyIcon(Components) {
                ContextMenuStrip = new ContextMenuStrip(),
                Icon = StoppedIcon,
                Visible = true
            };
            TrayIcon.ContextMenuStrip.Opening += OnContextMenu;
            TrayIcon.MouseDoubleClick += OnSettings;

            SyncWorker = new BackgroundWorker() {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = true,
            };

            SyncWorker.DoWork += RunSyncronizer;
            SyncWorker.ProgressChanged += UpdateProgress;
            SyncWorker.RunWorkerCompleted += CompleteSync;
            Components.Add(SyncWorker);

            StartItem = CreateItem("Start", OnStart);
            StopItem = CreateItem("Stop", OnStop, false);
        }

        private void ChangeIconToStartedFromThread() {
            TrayIcon.ContextMenuStrip.Invoke(new Action(ChangeIconToStarted));
        }

        private void ChangeIconToStarted() {
            TrayIcon.Icon = StartedIcon;
            StartItem.Enabled = false;
            StopItem.Enabled = true;
        }

        private void ChangeIconToStoppedFromThread() {
            TrayIcon.ContextMenuStrip.Invoke(new Action(ChangeIconToStopped));
        }

        private void ChangeIconToStopped() {
            TrayIcon.Icon = StoppedIcon;
            StartItem.Enabled = true;
            StopItem.Enabled = false;
        }

        private void ChangeIconToProcessingFromThread() {
            TrayIcon.ContextMenuStrip.Invoke(new Action(ChangeIconToProcessing));
        }

        private void ChangeIconToProcessing() {
            TrayIcon.Icon = InWorkIcon;
        }

        private void RunSyncronizer(object sender, DoWorkEventArgs e) {
            var worker = sender as BackgroundWorker;
            var syncronizer = new Core.InteractiveFileChangesSync() {
                User = Settings.User,
                Host = Settings.Host,
                SourcePath = Settings.SourcePath,
                DestinationPath = Settings.DestinationPath,
                PrivateKey = Settings.PrivateKey,
                Excludes = new HashSet<String>(Settings.Excludes.Cast<String>())
            };

            syncronizer.OnChangesProcessed += new Action(ChangeIconToProcessingFromThread);
            syncronizer.OnChangesWait += new Action(ChangeIconToStartedFromThread);
            syncronizer.OnStop += new Action(ChangeIconToStoppedFromThread);

            syncronizer.Start(() => worker.CancellationPending);
            e.Cancel = true;
            ChangeIconToStoppedFromThread();
        }

        private void UpdateProgress(object sender, ProgressChangedEventArgs e) {
            /// todo
        }

        private void CompleteSync(object sender, RunWorkerCompletedEventArgs e) {
        }

        protected override void Dispose(bool disposing) {
            if (disposing && Components != null) { Components.Dispose(); }
        }

        protected override void ExitThreadCore() {
            if (SettingsForm != null) {
                SettingsForm.Close();
            }

            TrayIcon.Visible = false;
            base.ExitThreadCore();
        }
    }

    static class Program {
        static Mutex mutex = new Mutex(true, "{8F6F0AC4-B9A1-45fd-A8CF-72F04E6BDE8F}");

        [STAThread]
        static void Main() {
            if (mutex.WaitOne(TimeSpan.Zero, true)) {
                try {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var applicationContext = new CustomApplicationContext();
                    Application.Run(applicationContext);
                } finally {
                    mutex.ReleaseMutex();
                }
            } else {
                MessageBox.Show("Already running.");
            }
        }
    }
}
