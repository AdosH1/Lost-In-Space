using Prominence.Model;
using Prominence.Model.Constants;
using Prominence.Model.Interfaces;
using Prominence.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Internals;
using Xamarin.Forms.Xaml;
using Xamarin.Essentials;

namespace Prominence.View
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class DialogueView : ContentPage
    {
        public bool IsReady = false;

        public DialogueView()
        {
            InitializeComponent();

            DialogueViewModel.AchievementTab = AchievementTab;
            AchievementTab.TranslateTo(0, -100);
        }

        protected override async void OnAppearing()
        {
            IsReady = true;
            var count = GetIEnumerableCount(LogCollection.ItemsSource);
            LogCollection.ScrollTo(count - 1, position: ScrollToPosition.End, animate: true);
        }

        private int GetIEnumerableCount(System.Collections.IEnumerable items)
        {
            var count = 0;
            var it = items.GetEnumerator();

            while (it.MoveNext())
                count++;
            return count;
        }
        
    }
}