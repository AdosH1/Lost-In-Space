using System;
using System.ComponentModel;
using Xamarin.Forms;

namespace LostInSpace.ViewModel
{
    public static class AudioViewModel : INotifyPropertyChanged
    {
        private static ImageSource _soundStateIcon { get; set; }
        public static ImageSource SoundStateIcon
        {
            get => _soundStateIcon;
            set
            {
                _soundStateIcon = value;
                NotifyPropertyChanged("SoundStateIcon");
            }
        }

        private static ImageSource _soundOnIcon { get; set; }
        public static ImageSource SoundOnIcon
        {
            get => _soundOnIcon;
            set
            {
                _soundOnIcon = value;
                NotifyPropertyChanged("SoundOnIcon");
            }
        }

        private static ImageSource _soundOffIcon { get; set; }
        public static ImageSource SoundOffIcon
        {
            get => _soundOffIcon;
            set
            {
                _soundOffIcon = value;
                NotifyPropertyChanged("SoundOffIcon");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
