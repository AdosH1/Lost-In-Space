using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prominence.ViewModel;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Prominence.View
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class MenuView : ContentPage
    {
        public MenuViewModel ViewModel { get; set; }

        public MenuView()
        {
            InitializeComponent();

            ViewModel = new MenuViewModel();
            BindingContext = ViewModel;
        }

        protected override async void OnAppearing()
        {
            ViewModel.SoundStateIcon = "Update";
        }
    }
}