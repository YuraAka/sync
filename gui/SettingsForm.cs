using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace gui {
    public partial class SettingsForm : Form {
        public SettingsForm(Settings data) {
            InitializeComponent();
            this.PropertyGrid.SelectedObject = data;
        }
    }
}
