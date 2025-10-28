using System;
using System.ComponentModel;

namespace UCXSyncTool.Models
{
    public class ActiveCopyViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string Node { get; set; } = string.Empty;
        public string Share { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? LastChange { get; set; }
        public string LogPath { get; set; } = string.Empty;
        // count of files downloaded in this session
        public int FilesDownloaded { get; set; }
        // progress percent (0-100) if total size known
        public double? ProgressPercent { get; set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propName));
        }
    }
}
