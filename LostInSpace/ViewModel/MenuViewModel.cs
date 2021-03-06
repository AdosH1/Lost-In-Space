using Prominence.Contexts;
using Prominence.Controllers;
using Prominence.Model;
using Prominence.Model.Constants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Xamarin.Forms;
using Sequoia;
using Core.Models;

namespace Prominence.ViewModel
{
    public class MenuViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<Achievement> Achievements { get; set; }
        private ImageSource _background { get; set; }
        public ImageSource Background
        {
            get
            {
                return _background;
            }
            set
            {
                _background = value;
                NotifyPropertyChanged("Background");
            }
        }
        private ImageSource _menuButtonImage { get; set; }
        public ImageSource MenuButtonImage
        {
            get => _menuButtonImage;
            set
            {
                _menuButtonImage = value;
                NotifyPropertyChanged("MenuButtonImage");
            }
        }

        public ImageSource SoundStateIcon
        {
            get => GameController.SoundStateIcon;
            set => NotifyPropertyChanged("SoundStateIcon");
        }

        private Command _toggleAudioCmd { get; set; }
        public Command ToggleAudioCmd
        {
            get => _toggleAudioCmd;
            set
            {
                _toggleAudioCmd = value;
                NotifyPropertyChanged("ToggleAudioCmd");
            }
        }

        private Command _teleporterCmd { get; set; }
        public Command TeleporterCmd
        {
            get => _teleporterCmd;
            set
            {
                _teleporterCmd = value;
                NotifyPropertyChanged("TeleporterCmd");
            }
        }

        private Command _closeMenuCmd { get; set; }
        public Command CloseMenuCmd
        {
            get => _closeMenuCmd;
            set
            {
                _closeMenuCmd = value;
                NotifyPropertyChanged("CloseMenuCmd");
            }
        }

        public MenuViewModel()
        {
            GameController.MenuViewModel = this;
            MenuButtonImage = AssemblyContext.GetImageByName(Constants.Gear);
            GameController.SoundOnIcon = AssemblyContext.GetImageByName(Constants.SoundOn);
            GameController.SoundOffIcon = AssemblyContext.GetImageByName(Constants.SoundOff);
            //SoundStateIcon = GameController.User.SettingsModel.MuteSound ? SoundOffIcon : SoundOnIcon;

            GameController.ChangeMenuBackground(Constants.MenuScreen);
            ToggleAudioCmd = new Command(async () =>
            {
                GameController.User.SettingsModel.MuteSound = !GameController.User.SettingsModel.MuteSound;
                HandleAudio(GameController.User.SettingsModel.MuteSound);
                NotifyPropertyChanged("SoundStateIcon");
            });
            TeleporterCmd = new Command(async () =>
            {
                GameController.DialogueViewModel.Log.Clear();
                var tp = GameController.Traverse(GameController.TeleporterLocation);
                GameController.DialogueViewModel.LoadFrame(tp);
                await Application.Current.MainPage.Navigation.PopModalAsync();
            });
            CloseMenuCmd = new Command(async () =>
            {
                await Application.Current.MainPage.Navigation.PopModalAsync();
            });

            LoadAchievements();
        }

        public void LoadAchievements()
        {
            Achievements = new ObservableCollection<Achievement>();
            foreach (var achievement in GameController.User.AchievementsModel.Achievements)
            {
                Achievements.Add(achievement.Value);
            }
        }

        public void ChangeBackground(ImageSource source)
        {
            Background = source;
        }

        private async void HandleAudio(bool muted)
        {
            if (!muted)
            {
                //SoundStateIcon = SoundOnIcon;
                GameController.PlayAudio();
            }
            else
            {
                //SoundStateIcon = SoundOffIcon;
                GameController.PauseAudio();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

    }
}
