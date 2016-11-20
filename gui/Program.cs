using System;
using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms.Design;
using System.Drawing.Design;

/// <summary>
/// TODO
/// - bad font
/// - fill settings
/// - change icon color
/// tabstops
/// icons
/// почему не обрабатывает копирования и удаления
/// </summary>
namespace gui {
    public class Settings {
        public string User { get; set; }

        [DisplayName("Private key path")]
        [EditorAttribute(
            typeof(System.Windows.Forms.Design.FileNameEditor),
            typeof(System.Drawing.Design.UITypeEditor))
        ]
        public string PrivateKey { get; set; }

        [DisplayName("Local directory path")]
        [EditorAttribute(typeof(FolderNameEditor), typeof(UITypeEditor))]
        public string SourcePath { get; set; }
        public string Host { get; set; }

        [DisplayName("Remote directory path")]
        public string DestinationPath { get; set; }
    };

    public class CustomApplicationContext : ApplicationContext {
        private readonly Icon StartedIcon = new Icon("active.ico");
        private readonly Icon StoppedIcon = new Icon("disabled.ico");
        private readonly Icon InWorkIcon = new Icon("working.ico");
        private ToolStripItem StartItem;
        private ToolStripItem StopItem;
        private BackgroundWorker SyncWorker;

        private SettingsForm SettingsForm;
        private Settings Settings = new Settings();
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

            StartItem = CreateItem("Start", OnStart);
            StopItem = CreateItem("Stop", OnStop, false);

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

            SyncWorker = new BackgroundWorker() {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = true,
            };

            SyncWorker.DoWork += RunSyncronizer;
            SyncWorker.ProgressChanged += UpdateProgress;
            SyncWorker.RunWorkerCompleted += CompleteSync;
            Components.Add(SyncWorker);
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
            };

            syncronizer.OnChangesProcessed += new Action(ChangeIconToProcessingFromThread);
            syncronizer.OnChangesWait += new Action(ChangeIconToStartedFromThread);

            syncronizer.Start(() => worker.CancellationPending);
            e.Cancel = true;
            TrayIcon.ContextMenuStrip.Invoke(new Action(ChangeIconToStopped));
            /// wait when everything is ready
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
        [STAThread]
        static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var applicationContext = new CustomApplicationContext();
            Application.Run(applicationContext);
        }
    }
}
