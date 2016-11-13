using System;
using System.Windows.Forms;
using System.Drawing;

namespace gui {
    public class CustomApplicationContext : ApplicationContext {
        private static readonly string IconFileName = "route.ico";

        private SettingsForm SettingsForm;
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
                SettingsForm = new SettingsForm();
                SettingsForm.Closed += OnCloseSettings;
                SettingsForm.Show();
            } else {
                SettingsForm.Activate();
            }
        }

        private void OnStart(object sender, EventArgs e) {
            MessageBox.Show("start");
        }

        private void OnStop(object sender, EventArgs e) {
            MessageBox.Show("stop");
        }

        private void OnExit(object sender, EventArgs e) {
            ExitThread();
        }

        private void OnContextMenu(object sender, System.ComponentModel.CancelEventArgs e) {
            e.Cancel = false;
            TrayIcon.ContextMenuStrip.Items.Clear();
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Settings", OnSettings));
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Start", OnStart));
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Stop", OnStop, false));
            TrayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            TrayIcon.ContextMenuStrip.Items.Add(CreateItem("Exit", OnExit));
        }

        private void InitializeContext() {
            Components = new System.ComponentModel.Container();
            TrayIcon = new NotifyIcon(Components) {
                ContextMenuStrip = new ContextMenuStrip(),
                Icon = new Icon(IconFileName),
                Visible = true
            };
            TrayIcon.ContextMenuStrip.Opening += OnContextMenu;
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
